using Mono.Cecil;
using Mono.Cecil.Cil;

// OniDevEnv — turns a copied ONI install into a debuggable, self-contained dev install.
//
//   scan  <ManagedDir>          list every call site that resolves a persistence root
//   patch <ManagedDir>          apply the debug + path-redirect patches (writes .orig backups)
//   verify <ManagedDir>         report current patch state
//
// Everything is done against the copy in the dev folder; originals are kept as *.orig.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: OniDevEnv <scan|patch|verify> <ManagedDir>");
    return 2;
}

string cmd = args[0];
string managed = args[1];
if (!Directory.Exists(managed))
{
    Console.Error.WriteLine($"no such directory: {managed}");
    return 2;
}

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(managed);
var readParams = new ReaderParameters { AssemblyResolver = resolver, ReadWrite = cmd == "patch" };

return cmd switch
{
    "scan" => Scan(managed, readParams),
    "patch" => Patch(managed, readParams),
    "verify" => Verify(managed, readParams),
    "stub" => Stub(args.Length > 2 ? args[2] : "DevDoorstop.dll", managed),
    _ => Usage(),
};

// ---------------------------------------------------------------- doorstop stub

// Doorstop always wants a target assembly to invoke. Emit a minimal one rather than pointing it at
// nothing: it drops a marker file next to the exe, which is the only cheap proof that the proxy DLL
// loaded and the runtime hook ran. Built with Cecil against the game's own mscorlib so Mono loads it.
static int Stub(string outFile, string managed)
{
    var lookup = new GameTypes(managed);

    var asm = AssemblyDefinition.CreateAssembly(
        new AssemblyNameDefinition("DevDoorstop", new Version(1, 0, 0, 0)),
        "DevDoorstop.dll", ModuleKind.Dll);
    var mod = asm.MainModule;

    var writeAllText = mod.ImportReference(lookup.Method("System.IO.File", "WriteAllText", 2));
    var objectCtor = mod.ImportReference(lookup.Ctor("System.Object", 0));

    var type = new TypeDefinition("Doorstop", "Entrypoint",
        TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
        mod.ImportReference(lookup.Type("System.Object")));
    mod.Types.Add(type);

    var ctor = new MethodDefinition(".ctor",
        MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName
        | MethodAttributes.RTSpecialName, mod.TypeSystem.Void);
    var cil = ctor.Body.GetILProcessor();
    cil.Emit(OpCodes.Ldarg_0);
    cil.Emit(OpCodes.Call, objectCtor);
    cil.Emit(OpCodes.Ret);
    type.Methods.Add(ctor);

    var start = new MethodDefinition("Start",
        MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
        mod.TypeSystem.Void);
    var il = start.Body.GetILProcessor();
    il.Emit(OpCodes.Ldstr, "doorstop_loaded.txt");
    il.Emit(OpCodes.Ldstr, "Doorstop ran; mono debugger server should be listening.");
    il.Emit(OpCodes.Call, writeAllText);
    il.Emit(OpCodes.Ret);
    type.Methods.Add(start);

    asm.Write(outFile);
    Console.WriteLine($"wrote {outFile} (Doorstop.Entrypoint.Start)");
    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("usage: OniDevEnv <scan|patch|verify> <ManagedDir>");
    return 2;
}

// ---------------------------------------------------------------- scan

static int Scan(string managed, ReaderParameters rp)
{
    // Anything that can name a directory outside the game folder.
    string[] interesting =
    {
        "UnityEngine.Application::get_persistentDataPath",
        "UnityEngine.Application::get_dataPath",
        "UnityEngine.Application::get_consoleLogPath",
        "UnityEngine.Application::get_temporaryCachePath",
        "System.Environment::GetFolderPath",
        "Util::GetKleiRootPath",
        "Util::RootFolder",
        "Util::CacheFolder",
        "System.IO.Path::GetTempPath",
        "Ionic.Zip.ZipFile::.ctor",
        "Ionic.Zip.ZipFile::Read",
        "OniDevEnv.DevPaths::LocalizeWorkshopFile",
        "UnityEngine.Debug::get_isDebugBuild",
    };

    foreach (var file in Directory.GetFiles(managed, "*.dll").OrderBy(f => f))
    {
        string name = Path.GetFileName(file);
        if (!name.StartsWith("Assembly-CSharp") && name != "Klei.dll") continue;

        using var asm = AssemblyDefinition.ReadAssembly(file, rp);
        foreach (var type in AllTypes(asm.MainModule))
        foreach (var m in type.Methods)
        {
            if (!m.HasBody) continue;
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.Operand is not MethodReference mr) continue;
                string full = mr.DeclaringType.FullName + "::" + mr.Name;
                if (interesting.Any(full.Contains))
                    Console.WriteLine($"{name,-32} {type.FullName}::{m.Name}  ->  {full}");
            }
        }
    }
    return 0;
}

static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition mod)
{
    foreach (var t in mod.Types)
    {
        yield return t;
        foreach (var n in Nested(t)) yield return n;
    }
    static IEnumerable<TypeDefinition> Nested(TypeDefinition t)
    {
        foreach (var n in t.NestedTypes)
        {
            yield return n;
            foreach (var d in Nested(n)) yield return d;
        }
    }
}

// ---------------------------------------------------------------- patch

static int Patch(string managed, ReaderParameters rp)
{
    // Both restores happen before GameTypes reads the folder, so it sees the stock assemblies.
    Backup(Path.Combine(managed, "Assembly-CSharp-firstpass.dll"), rp);
    Backup(Path.Combine(managed, "Assembly-CSharp.dll"), rp);

    var lookup = new GameTypes(managed);
    int changed = 0;
    changed += PatchFirstpass(managed, rp, lookup);
    changed += PatchMain(managed, rp, lookup);
    Console.WriteLine($"done, {changed} assemblies rewritten");
    return 0;
}

// Leave `file` holding the stock assembly and `.orig` holding a copy of it.
//
// Which way the copy goes is decided by the file itself, never by the presence of `.orig`: a game
// update overwrites the patched DLL with a fresh stock one but leaves the now-stale `.orig` behind,
// and restoring that would silently downgrade the assembly to the previous game version.
static void Backup(string file, ReaderParameters rp)
{
    string orig = file + ".orig";
    bool patched;
    using (var probe = AssemblyDefinition.ReadAssembly(
               file, new ReaderParameters { AssemblyResolver = rp.AssemblyResolver }))
        patched = IsPatched(probe.MainModule);

    if (!patched) File.Copy(file, orig, true);          // stock: (re)take the baseline
    else if (File.Exists(orig)) File.Copy(orig, file, true);  // ours: rewind before re-patching
}

// Markers we inject ourselves, so this cannot be confused with anything the stock build carries.
static bool IsPatched(ModuleDefinition mod)
{
    if (mod.GetType("OniDevEnv.DevPaths") != null) return true;
    var root = mod.GetType("Util")?.Methods.FirstOrDefault(m => m.Name == "GetKleiRootPath");
    return root != null && root.HasBody
           && root.Body.Instructions.Any(i => (i.Operand as string) == "DevData");
}

// Assembly-CSharp-firstpass holds Util. Redirect the root and mark it debuggable.
static int PatchFirstpass(string managed, ReaderParameters rp, GameTypes lookup)
{
    string file = Path.Combine(managed, "Assembly-CSharp-firstpass.dll");
    using var asm = AssemblyDefinition.ReadAssembly(file, rp);
    var mod = asm.MainModule;

    var util = mod.GetType("Util") ?? throw new Exception("Util not found in firstpass");

    // Application.dataPath is "<gameroot>/OxygenNotIncluded_Data"; its parent is the game root,
    // so the whole profile lands in <gameroot>/DevData and travels with the copied install.
    // Every reference is imported from the game's own Managed folder — importing from the host
    // .NET runtime instead would emit references to System.Private.CoreLib, which Mono has not got.
    var dataPath = mod.ImportReference(lookup.Method("UnityEngine.Application", "get_dataPath", 0));
    var getDirName = mod.ImportReference(lookup.Method("System.IO.Path", "GetDirectoryName", 1));
    var combine = mod.ImportReference(lookup.Method("System.IO.Path", "Combine", 2));

    // string GetKleiRootPath() => Path.Combine(Path.GetDirectoryName(Application.dataPath), "DevData");
    var root = util.Methods.First(m => m.Name == "GetKleiRootPath" && m.Parameters.Count == 0);
    RewriteToDevData(root, dataPath, getDirName, combine, mod);

    // defaultRootFolder (= Application.persistentDataPath) feeds CacheFolder and the non-Windows
    // branch of GetKleiRootPath; point it at the same place so nothing escapes to AppData.
    var cctor = util.Methods.FirstOrDefault(m => m.Name == ".cctor");
    if (cctor != null) RedirectPersistentDataPath(cctor, dataPath, getDirName, combine, mod);

    SetDebuggable(asm, lookup);
    asm.Write();
    Console.WriteLine("patched Assembly-CSharp-firstpass.dll (Util root -> <gameroot>/DevData, debuggable)");
    return 1;
}

static void RewriteToDevData(MethodDefinition m, MethodReference dataPath, MethodReference getDirName,
                             MethodReference combine, ModuleDefinition mod)
{
    m.Body.Instructions.Clear();
    m.Body.ExceptionHandlers.Clear();
    m.Body.Variables.Clear();
    var il = m.Body.GetILProcessor();
    il.Emit(OpCodes.Call, dataPath);
    il.Emit(OpCodes.Call, getDirName);
    il.Emit(OpCodes.Ldstr, "DevData");
    il.Emit(OpCodes.Call, combine);
    il.Emit(OpCodes.Ret);
}

// Replace `call Application::get_persistentDataPath` with the DevData path, everywhere in a method.
static void RedirectPersistentDataPath(MethodDefinition m, MethodReference dataPath, MethodReference getDirName,
                                       MethodReference combine, ModuleDefinition mod)
{
    var il = m.Body.GetILProcessor();
    foreach (var ins in m.Body.Instructions.ToList())
    {
        if (ins.Operand is not MethodReference mr) continue;
        if (mr.Name != "get_persistentDataPath") continue;
        // Replace drops `ins` from the list, so the replacement becomes the anchor for the rest.
        var after = Instruction.Create(OpCodes.Call, dataPath);
        il.Replace(ins, after);
        after = InsertAfter(il, after, Instruction.Create(OpCodes.Call, getDirName));
        after = InsertAfter(il, after, Instruction.Create(OpCodes.Ldstr, "DevData"));
        InsertAfter(il, after, Instruction.Create(OpCodes.Call, combine));
    }
}

static Instruction InsertAfter(ILProcessor il, Instruction anchor, Instruction added)
{
    il.InsertAfter(anchor, added);
    return added;
}

// ---------------------------------------------------------------- debuggable

// ---------------------------------------------------------------- Assembly-CSharp

// Assembly-CSharp holds the two paths that escape the DevData root even after Util is redirected:
// the LocalApplicationData write-probe, and Steam Workshop mod content.
static int PatchMain(string managed, ReaderParameters rp, GameTypes lookup)
{
    string file = Path.Combine(managed, "Assembly-CSharp.dll");
    using var asm = AssemblyDefinition.ReadAssembly(file, rp);
    var mod = asm.MainModule;

    var dataPath = mod.ImportReference(lookup.Method("UnityEngine.Application", "get_dataPath", 0));
    var getDirName = mod.ImportReference(lookup.Method("System.IO.Path", "GetDirectoryName", 1));
    var combine = mod.ImportReference(lookup.Method("System.IO.Path", "Combine", 2));

    int probes = RedirectLocalAppData(mod, dataPath, getDirName, combine);
    LogBuildFlags(mod, lookup);
    var localize = AddDevPaths(mod, lookup, dataPath, getDirName, combine);
    int zips = InjectWorkshopRedirect(mod, localize);

    SetDebuggable(asm, lookup);
    asm.Write();
    Console.WriteLine($"patched Assembly-CSharp.dll (debuggable, {probes} data-location probe(s) "
                      + $"-> DevData, {zips} Workshop zip open(s) mirrored)");
    if (probes != 1) Console.Error.WriteLine($"  warning: expected 1 GetFolderPath in Global.TestDataLocations, patched {probes}");
    if (zips != 2) Console.Error.WriteLine($"  warning: expected 2 Workshop zip call sites, patched {zips}");
    return 1;
}

// Debug.isDebugBuild reflects BuildSettings.isDebugBuild in globalgamemanagers — the flag that
// decides the "Development Build" watermark and the player-connection listener. It is baked into
// the player data, so no runtime patch can set it; log it instead, because that is the only way to
// tell from outside the window whether an edit to globalgamemanagers actually took.
//
//   Debug.Log((object)("OniDevEnv: isDebugBuild=" + (object)Debug.isDebugBuild));
//
// Prepended to Global.TestDataLocations, which already runs early on every Windows boot.
static void LogBuildFlags(ModuleDefinition mod, GameTypes lookup)
{
    var m = mod.GetType("Global")?.Methods.FirstOrDefault(x => x.Name == "TestDataLocations");
    if (m == null || !m.HasBody) return;

    var isDebugBuild = mod.ImportReference(lookup.Method("UnityEngine.Debug", "get_isDebugBuild", 0));
    var concat = mod.ImportReference(lookup.Method("System.String", "Concat",
                                                   "System.Object", "System.Object"));
    var log = mod.ImportReference(lookup.Method("UnityEngine.Debug", "Log", "System.Object"));

    var il = m.Body.GetILProcessor();
    var first = m.Body.Instructions[0];
    foreach (var ins in new[]
             {
                 Instruction.Create(OpCodes.Ldstr, "OniDevEnv: isDebugBuild="),
                 Instruction.Create(OpCodes.Call, isDebugBuild),
                 Instruction.Create(OpCodes.Box, mod.TypeSystem.Boolean),
                 Instruction.Create(OpCodes.Call, concat),
                 Instruction.Create(OpCodes.Call, log),
             })
        il.InsertBefore(first, ins);
}

// Global.TestDataLocations() probes %LOCALAPPDATA%\Klei\<title> at startup — it creates the
// directory tree, writes a file, reads it back and deletes it, leaving the empty tree behind.
// Drop the SpecialFolder argument and hand it the DevData root instead, so the probe still runs
// (its result is logged, and a failure is worth seeing) but lands inside the dev folder.
static int RedirectLocalAppData(ModuleDefinition mod, MethodReference dataPath,
                                MethodReference getDirName, MethodReference combine)
{
    var m = mod.GetType("Global")?.Methods.FirstOrDefault(x => x.Name == "TestDataLocations");
    if (m == null || !m.HasBody) return 0;

    int n = 0;
    var il = m.Body.GetILProcessor();
    foreach (var ins in m.Body.Instructions.ToList())
    {
        if (ins.Operand is not MethodReference mr || mr.Name != "GetFolderPath") continue;
        var pop = Instruction.Create(OpCodes.Pop);   // discard the pushed SpecialFolder enum
        il.Replace(ins, pop);
        var a = InsertAfter(il, pop, Instruction.Create(OpCodes.Call, dataPath));
        a = InsertAfter(il, a, Instruction.Create(OpCodes.Call, getDirName));
        a = InsertAfter(il, a, Instruction.Create(OpCodes.Ldstr, "DevData"));
        InsertAfter(il, a, Instruction.Create(OpCodes.Call, combine));
        n++;
    }
    return n;
}

// Steam downloads Workshop items into a library-wide tree that every install of the game shares.
// A Workshop mod is a single zip, and both readers get its path from SteamUGC.GetItemInstallInfo,
// so one helper is enough: mirror the zip into DevData\workshop_mods and hand back the local copy.
//
//   static string LocalizeWorkshopFile(string src)
//   {
//       try
//       {
//           string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "DevData", "workshop_mods");
//           Directory.CreateDirectory(dir);
//           string dst = Path.Combine(dir, Path.GetFileName(Path.GetDirectoryName(src)) + "_" + Path.GetFileName(src));
//           File.Copy(src, dst, true);
//           return dst;
//       }
//       catch { return src; }   // unreadable / vanished / locked: behave exactly as stock
//   }
//
// The parent directory name is the PublishedFileId, so the mirrored names stay unique per mod.
static MethodReference AddDevPaths(ModuleDefinition mod, GameTypes lookup, MethodReference dataPath,
                                   MethodReference getDirName, MethodReference combine)
{
    var str = mod.TypeSystem.String;
    var getFileName = mod.ImportReference(lookup.Method("System.IO.Path", "GetFileName", 1));
    var createDir = mod.ImportReference(lookup.Method("System.IO.Directory", "CreateDirectory", 1));
    var concat3 = mod.ImportReference(lookup.Method("System.String", "Concat", 3));
    var fileCopy = mod.ImportReference(lookup.Method("System.IO.File", "Copy",
                                                     "System.String", "System.String", "System.Boolean"));
    var exception = mod.ImportReference(lookup.Type("System.Exception"));

    var type = new TypeDefinition("OniDevEnv", "DevPaths",
        TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed
        | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
        mod.ImportReference(lookup.Type("System.Object")));
    mod.Types.Add(type);

    var m = new MethodDefinition("LocalizeWorkshopFile",
        MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, str);
    m.Parameters.Add(new ParameterDefinition("src", ParameterAttributes.None, str));
    type.Methods.Add(m);

    var body = m.Body;
    body.InitLocals = true;
    body.Variables.Add(new VariableDefinition(str));   // 0: dir
    body.Variables.Add(new VariableDefinition(str));   // 1: dst
    body.Variables.Add(new VariableDefinition(str));   // 2: result
    var il = body.GetILProcessor();

    var exit = Instruction.Create(OpCodes.Ldloc_2);

    var tryStart = Instruction.Create(OpCodes.Call, dataPath);
    il.Append(tryStart);
    il.Emit(OpCodes.Call, getDirName);
    il.Emit(OpCodes.Ldstr, "DevData");
    il.Emit(OpCodes.Call, combine);
    il.Emit(OpCodes.Ldstr, "workshop_mods");
    il.Emit(OpCodes.Call, combine);
    il.Emit(OpCodes.Stloc_0);
    il.Emit(OpCodes.Ldloc_0);
    il.Emit(OpCodes.Call, createDir);
    il.Emit(OpCodes.Pop);
    il.Emit(OpCodes.Ldloc_0);
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Call, getDirName);
    il.Emit(OpCodes.Call, getFileName);
    il.Emit(OpCodes.Ldstr, "_");
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Call, getFileName);
    il.Emit(OpCodes.Call, concat3);
    il.Emit(OpCodes.Call, combine);
    il.Emit(OpCodes.Stloc_1);
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Ldloc_1);
    il.Emit(OpCodes.Ldc_I4_1);
    il.Emit(OpCodes.Call, fileCopy);
    il.Emit(OpCodes.Ldloc_1);
    il.Emit(OpCodes.Stloc_2);
    il.Append(Instruction.Create(OpCodes.Leave, exit));

    var handlerStart = Instruction.Create(OpCodes.Pop);   // drop the exception object
    il.Append(handlerStart);
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Stloc_2);
    il.Append(Instruction.Create(OpCodes.Leave, exit));

    il.Append(exit);
    il.Emit(OpCodes.Ret);

    body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
    {
        CatchType = exception,
        TryStart = tryStart,
        TryEnd = handlerStart,
        HandlerStart = handlerStart,
        HandlerEnd = exit,
    });

    return m;
}

// At both call sites the zip path is already the top of the stack, so the whole injection is one
// `call` inserted in front of the ZipFile open. Restricted to the two types that read Workshop
// content — a blanket rewrite would also catch zips the game writes.
static int InjectWorkshopRedirect(ModuleDefinition mod, MethodReference localize)
{
    int n = 0;
    foreach (var typeName in new[] { "KMod.Steam", "SteamUGCService" })
    {
        var type = mod.GetType(typeName);
        if (type == null) { Console.Error.WriteLine($"  warning: {typeName} not found"); continue; }

        foreach (var m in type.Methods)
        {
            if (!m.HasBody) continue;
            var il = m.Body.GetILProcessor();
            foreach (var ins in m.Body.Instructions.ToList())
            {
                if (ins.Operand is not MethodReference mr) continue;
                if (mr.DeclaringType.Name != "ZipFile") continue;
                // KMod.ZipFile is a struct, so its constructor arrives as `call .ctor` after an
                // `ldloca`, not as `newobj` — matching only newobj silently misses MakeMod.
                if (ins.OpCode != OpCodes.Newobj && ins.OpCode != OpCodes.Call) continue;
                if (mr.Name != ".ctor" && mr.Name != "Read") continue;
                if (mr.Parameters.Count != 1
                    || mr.Parameters[0].ParameterType.FullName != "System.String") continue;
                il.InsertBefore(ins, Instruction.Create(OpCodes.Call, localize));
                n++;
            }
        }
    }
    return n;
}

// [assembly: Debuggable(DebuggingModes.Default | DisableOptimizations |
//                       IgnoreSymbolStoreSequencePoints | EnableEditAndContinue)]
// Without DisableOptimizations the JIT inlines and drops locals, so breakpoints land in the
// wrong place and most variables read as "optimized out" in dnSpy.
static void SetDebuggable(AssemblyDefinition asm, GameTypes lookup)
{
    var mod = asm.MainModule;
    const int modes = 0x1 /*Default*/ | 0x2 /*IgnoreSymbolStoreSequencePoints*/
                    | 0x4 /*EnableEditAndContinue*/ | 0x100 /*DisableOptimizations*/;
    const string attrName = "System.Diagnostics.DebuggableAttribute";

    var ctor = mod.ImportReference(lookup.Ctor(attrName, 1));
    var modesType = mod.ImportReference(lookup.Type(attrName + "/DebuggingModes"));

    var existing = asm.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == attrName);
    if (existing != null) asm.CustomAttributes.Remove(existing);

    var ca = new CustomAttribute(ctor);
    ca.ConstructorArguments.Add(new CustomAttributeArgument(modesType, modes));
    asm.CustomAttributes.Add(ca);
}

// ---------------------------------------------------------------- verify

static int Verify(string managed, ReaderParameters rp)
{
    foreach (var name in new[] { "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll" })
    {
        string file = Path.Combine(managed, name);
        using var asm = AssemblyDefinition.ReadAssembly(file, rp);
        var dbg = asm.CustomAttributes.FirstOrDefault(
            a => a.AttributeType.FullName == "System.Diagnostics.DebuggableAttribute");
        int modes = dbg == null ? 0 : Convert.ToInt32(dbg.ConstructorArguments[0].Value);
        Console.WriteLine($"{name,-34} Debuggable=0x{modes:X}  DisableOptimizations={(modes & 0x100) != 0}");

        var mod = asm.MainModule;

        var root = mod.GetType("Util")?.Methods.FirstOrDefault(m => m.Name == "GetKleiRootPath");
        if (root != null)
        {
            bool redirected = root.Body.Instructions.Any(i => (i.Operand as string) == "DevData");
            bool stillDocs = root.Body.Instructions.Any(
                i => i.Operand is MethodReference mr && mr.Name == "GetFolderPath");
            Console.WriteLine($"{"  Util.GetKleiRootPath",-34} DevData={redirected}  MyDocuments={stillDocs}");
        }

        var probe = mod.GetType("Global")?.Methods.FirstOrDefault(m => m.Name == "TestDataLocations");
        if (probe != null)
        {
            bool localAppData = probe.Body.Instructions.Any(
                i => i.Operand is MethodReference mr && mr.Name == "GetFolderPath");
            Console.WriteLine($"{"  Global.TestDataLocations",-34} LocalAppData={localAppData}");
        }

        if (mod.GetType("SteamUGCService") != null)
        {
            int sites = 0, hooked = 0;
            foreach (var t in new[] { "KMod.Steam", "SteamUGCService" })
            foreach (var m in mod.GetType(t)?.Methods ?? Enumerable.Empty<MethodDefinition>())
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.Operand is not MethodReference mr || mr.DeclaringType.Name != "ZipFile") continue;
                    if (mr.Name != ".ctor" && mr.Name != "Read") continue;
                    if (mr.Parameters.Count != 1) continue;
                    sites++;
                    if (ins.Previous?.Operand is MethodReference p && p.Name == "LocalizeWorkshopFile") hooked++;
                }
            }
            Console.WriteLine($"{"  Workshop zip opens",-34} {hooked}/{sites} mirrored into DevData");
        }
    }
    return 0;
}

sealed class GameTypes
{
    readonly List<AssemblyDefinition> assemblies = new();

    public GameTypes(string managed)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        // InMemory: read each file whole and let go of it. A handle held here stops the patch
        // below from opening the same assembly for writing on Windows (sharing violation);
        // through WSL's file bridge there are no sharing rules, which is why it went unseen.
        var rp = new ReaderParameters { AssemblyResolver = resolver, InMemory = true };
        foreach (var f in Directory.GetFiles(managed, "*.dll"))
        {
            try { assemblies.Add(AssemblyDefinition.ReadAssembly(f, rp)); }
            catch { /* native or otherwise unreadable; not a metadata assembly */ }
        }
    }

    public MethodDefinition Method(string typeName, string methodName, int argc)
    {
        foreach (var asm in assemblies)
        {
            var td = asm.MainModule.GetType(typeName);
            var md = td?.Methods.FirstOrDefault(m => m.Name == methodName
                                                     && m.Parameters.Count == argc
                                                     && m.Parameters.All(p => p.ParameterType.FullName == "System.String"
                                                                              || argc == 0));
            if (md != null) return md;
        }
        throw new Exception($"{typeName}::{methodName}/{argc} not found in Managed folder");
    }

    // Overload resolution by explicit parameter types, for the signatures the all-string filter
    // above cannot express (File.Copy(string, string, bool) is the one that forced this).
    public MethodDefinition Method(string typeName, string methodName, params string[] paramTypes)
    {
        foreach (var asm in assemblies)
        {
            var td = asm.MainModule.GetType(typeName);
            var md = td?.Methods.FirstOrDefault(
                m => m.Name == methodName
                     && m.Parameters.Count == paramTypes.Length
                     && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(paramTypes));
            if (md != null) return md;
        }
        throw new Exception($"{typeName}::{methodName}({string.Join(", ", paramTypes)}) not found in Managed folder");
    }

    public TypeDefinition Type(string typeName)
    {
        foreach (var asm in assemblies)
        {
            var td = asm.MainModule.GetType(typeName);
            if (td != null) return td;
        }
        throw new Exception($"{typeName} not found in Managed folder");
    }

    public MethodDefinition Ctor(string typeName, int argc)
        => Type(typeName).Methods.First(m => m.IsConstructor && m.Parameters.Count == argc);
}
