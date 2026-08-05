// SMSLIBRE Stage 1 — assembly classifier + batch decompiler.
//
// Wraps ICSharpCode.Decompiler (the ILSpy engine) directly rather than using the
// ilspycmd tool, whose 8.x build crashes on these assemblies inside
// DecompilerTypeSystem when formatting a target-framework version string.
//
// Two modes:
//   classify   <directory> <out.csv>
//       Walks the tree and separates native / pure-IL / mixed-mode (C++/CLI)
//       assemblies. This matters because a CLR header alone does not mean the
//       code is recoverable: C++/CLI assemblies carry native machine code that
//       no IL decompiler can reach.
//
//   decompile  <assembly|directory> <out-dir> [--filter <substring>]
//       Emits one .cs file per top-level type, foldered by namespace.

using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: decompiler classify  <directory> <out.csv>");
    Console.Error.WriteLine("       decompiler decompile <assembly|directory> <out-dir> [--filter <substring>]");
    Console.Error.WriteLine("       decompiler deps     <directory> <out-prefix>   (writes <prefix>-refs.csv, <prefix>-pinvoke.csv)");
    return 2;
}

return args[0] switch
{
    "classify" => Classify(args[1], args[2]),
    "decompile" => Decompile(args[1], args[2], GetOpt(args, "--filter")),
    "deps" => Deps(args[1], args[2]),
    _ => Fail()
};

static int Fail() { Console.Error.WriteLine("unknown mode"); return 2; }

static string? GetOpt(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

static int Classify(string root, string outCsv)
{
    var files = Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories)
        .Concat(Directory.GetFiles(root, "*.exe", SearchOption.AllDirectories))
        .OrderBy(f => f).ToArray();

    var sb = new StringBuilder("RelPath,Name,SizeKB,Kind,Machine,CorFlags,TargetFramework,AssemblyName,AssemblyVersion,TypeCount,MethodCount,PInvokeCount,HasCppDetails,Note\n");
    int native = 0, pure = 0, mixed = 0, bad = 0;

    foreach (var path in files)
    {
        string rel = Path.GetRelativePath(root, path);
        string name = Path.GetFileName(path);
        long sizeKb = new FileInfo(path).Length / 1024;

        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs, PEStreamOptions.PrefetchEntireImage);

            string machine = pe.PEHeaders.CoffHeader.Machine.ToString();

            if (!pe.HasMetadata)
            {
                native++;
                sb.Append($"{Csv(rel)},{Csv(name)},{sizeKb},native,{machine},,,,,0,0,0,false,\n");
                continue;
            }

            var corHeader = pe.PEHeaders.CorHeader!;
            var flags = corHeader.Flags;
            bool ilOnly = (flags & CorFlags.ILOnly) != 0;

            var md = pe.GetMetadataReader();

            int typeCount = md.TypeDefinitions.Count;
            int methodCount = md.MethodDefinitions.Count;

            // Methods whose IL RVA is 0 and that are not abstract/pinvoke are
            // native bodies — the hallmark of C++/CLI mixed mode.
            int pinvoke = 0;
            foreach (var h in md.MethodDefinitions)
            {
                var m = md.GetMethodDefinition(h);
                if ((m.Attributes & MethodAttributes.PinvokeImpl) != 0) pinvoke++;
            }

            bool hasCpp = false;
            foreach (var h in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(h);
                string ns = md.GetString(td.Namespace);
                if (ns.Contains("CppImplementationDetails") || ns.Contains("CrtImplementationDetails"))
                { hasCpp = true; break; }
            }

            string tfm = "";
            foreach (var h in md.CustomAttributes)
            {
                var ca = md.GetCustomAttribute(h);
                if (ca.Parent.Kind != HandleKind.AssemblyDefinition) continue;
                try
                {
                    if (ca.Constructor.Kind == HandleKind.MemberReference)
                    {
                        var mr = md.GetMemberReference((MemberReferenceHandle)ca.Constructor);
                        if (mr.Parent.Kind == HandleKind.TypeReference)
                        {
                            var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
                            if (md.GetString(tr.Name) == "TargetFrameworkAttribute")
                            {
                                var val = md.GetBlobReader(ca.Value);
                                val.ReadUInt16();
                                tfm = val.ReadSerializedString() ?? "";
                            }
                        }
                    }
                }
                catch { /* attribute blob shapes vary; best-effort only */ }
            }

            string asmName = "", asmVer = "";
            if (md.IsAssembly)
            {
                var ad = md.GetAssemblyDefinition();
                asmName = md.GetString(ad.Name);
                asmVer = ad.Version.ToString();
            }

            string kind;
            if (ilOnly && !hasCpp) { kind = "pure-IL"; pure++; }
            else { kind = "mixed-mode"; mixed++; }

            sb.Append($"{Csv(rel)},{Csv(name)},{sizeKb},{kind},{machine},{flags},{Csv(tfm)},{Csv(asmName)},{asmVer},{typeCount},{methodCount},{pinvoke},{hasCpp},\n");
        }
        catch (Exception ex)
        {
            bad++;
            sb.Append($"{Csv(rel)},{Csv(name)},{sizeKb},error,,,,,,0,0,0,false,{Csv(ex.Message)}\n");
        }
    }

    File.WriteAllText(outCsv, sb.ToString());
    Console.WriteLine($"native={native}  pure-IL={pure}  mixed-mode={mixed}  error={bad}");
    Console.WriteLine($"Wrote {outCsv}");
    return 0;
}

static int Decompile(string input, string outRoot, string? filter)
{
    var targets = Directory.Exists(input)
        ? Directory.GetFiles(input, "*.dll", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(input, "*.exe", SearchOption.TopDirectoryOnly))
            .OrderBy(f => f).ToArray()
        : new[] { input };

    // Filter is a regex over the file name, so first-party prefixes can be
    // anchored (a substring match on "AL" would also hit Globalization etc).
    if (filter is not null)
    {
        var re = new System.Text.RegularExpressions.Regex(filter,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        targets = targets.Where(t => re.IsMatch(Path.GetFileName(t))).ToArray();
    }

    Directory.CreateDirectory(outRoot);
    var report = new StringBuilder("Assembly,Status,Types,Files,Errors,Bytes,Note\n");

    // Search directory so cross-assembly references resolve; unresolved refs
    // degrade output quality but are not fatal.
    string searchDir = Directory.Exists(input) ? input : Path.GetDirectoryName(Path.GetFullPath(input))!;

    foreach (var path in targets)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        Console.Write($"[{name}] ");

        try
        {
            var file = new PEFile(path, PEStreamOptions.PrefetchEntireImage);
            if (!file.Reader.HasMetadata)
            {
                Console.WriteLine("native, skipped");
                report.Append($"{Csv(name)},skipped,0,0,0,0,native PE\n");
                continue;
            }

            var resolver = new UniversalAssemblyResolver(path, throwOnError: false,
                targetFramework: file.DetectTargetFrameworkId());
            resolver.AddSearchDirectory(searchDir);

            var settings = new DecompilerSettings(LanguageVersion.CSharp10_0)
            {
                ThrowOnAssemblyResolveErrors = false,
                RemoveDeadCode = false,
                RemoveDeadStores = false,
                ShowXmlDocumentation = true,
                UseSdkStyleProjectFormat = false,
            };

            var decompiler = new CSharpDecompiler(file, resolver, settings);
            var md = file.Metadata;

            string asmOut = Path.Combine(outRoot, name);
            Directory.CreateDirectory(asmOut);

            int types = 0, written = 0, errors = 0;
            long bytes = 0;

            var byNamespace = new Dictionary<string, List<TypeDefinitionHandle>>();
            foreach (var handle in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(handle);
                if (!td.GetDeclaringType().IsNil) continue;   // nested — emitted with its parent
                string ns = md.GetString(td.Namespace);
                if (!byNamespace.TryGetValue(ns, out var list))
                    byNamespace[ns] = list = new List<TypeDefinitionHandle>();
                list.Add(handle);
                types++;
            }

            foreach (var kv in byNamespace)
            {
                string ns = kv.Key;
                // C++/CLI emits function-local types whose "namespace" is the
                // full mangled signature — hundreds of characters. Cap it.
                string nsDir = string.IsNullOrEmpty(ns) ? asmOut : Path.Combine(asmOut, ShortenSegment(Sanitise(ns)));
                Directory.CreateDirectory(nsDir);

                foreach (var handle in kv.Value)
                {
                    var td = md.GetTypeDefinition(handle);
                    string typeName = md.GetString(td.Name);
                    string stem = Sanitise(typeName);
                    if (stem.Length > 80) stem = stem.Substring(0, 80);

                    string dest = Path.Combine(nsDir, stem + ".cs");
                    if (dest.Length > 240)
                        dest = Path.Combine(nsDir, "T_" + MetadataTokens.GetToken(handle).ToString("X8") + ".cs");
                    int dup = 1;
                    while (File.Exists(dest) || File.Exists(dest + ".error.txt"))
                        dest = Path.Combine(nsDir, stem + "_" + (++dup) + ".cs");

                    try
                    {
                        string code = decompiler.DecompileTypeAsString(
                            new FullTypeName(string.IsNullOrEmpty(ns) ? typeName : ns + "." + typeName));
                        File.WriteAllText(dest, code);
                        written++;
                        bytes += code.Length;
                    }
                    catch (Exception ex)
                    {
                        File.WriteAllText(dest + ".error.txt", ex.ToString());
                        errors++;
                    }
                }
            }

            Console.WriteLine($"{types} types -> {written} files ({errors} err), {bytes / 1024} KB");
            report.Append($"{Csv(name)},ok,{types},{written},{errors},{bytes},\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED: {ex.Message}");
            report.Append($"{Csv(name)},failed,0,0,0,0,{Csv(ex.Message)}\n");
        }
    }

    File.WriteAllText(Path.Combine(outRoot, "decompile-report.csv"), report.ToString());
    Console.WriteLine($"\nReport: {Path.Combine(outRoot, "decompile-report.csv")}");
    return 0;
}

static int Deps(string root, string outPrefix)
{
    var files = Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories)
        .Concat(Directory.GetFiles(root, "*.exe", SearchOption.AllDirectories))
        .OrderBy(f => f).ToArray();

    // WPF / WinForms / other Windows-only framework assemblies. Any managed
    // assembly that references one of these is UI/Windows-coupled and cannot run
    // on native Linux .NET unchanged.
    var winOnlyRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "PresentationFramework", "PresentationCore", "WindowsBase", "PresentationUI",
        "System.Xaml", "ReachFramework", "System.Windows.Forms", "System.Windows.Forms.Primitives",
        "System.Windows.Controls.Ribbon", "UIAutomationProvider", "UIAutomationTypes",
        "System.Windows.Presentation", "System.Windows.Input.Manipulations",
        "DirectWriteForwarder", "System.Printing", "Microsoft.Win32.SystemEvents",
        "System.Drawing.Common", "Accessibility", "PresentationFramework.Aero2",
    };

    var refs = new StringBuilder("Assembly,RefAssembly,RefVersion,RefIsWindowsOnly\n");
    var pinv = new StringBuilder("Assembly,NativeDll,PInvokeCount\n");
    int scanned = 0;

    foreach (var path in files)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs, PEStreamOptions.PrefetchEntireImage);
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            scanned++;

            foreach (var h in md.AssemblyReferences)
            {
                var ar = md.GetAssemblyReference(h);
                string rn = md.GetString(ar.Name);
                bool win = winOnlyRefs.Contains(rn);
                refs.Append($"{Csv(name)},{Csv(rn)},{ar.Version},{win}\n");
            }

            // P/Invoke targets: methods flagged PinvokeImpl carry an ImplMap
            // naming the native module they bind to.
            var byDll = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in md.MethodDefinitions)
            {
                var m = md.GetMethodDefinition(h);
                if ((m.Attributes & MethodAttributes.PinvokeImpl) == 0) continue;
                var import = m.GetImport();
                if (import.Module.IsNil) continue;
                string dll = md.GetString(md.GetModuleReference(import.Module).Name);
                byDll[dll] = byDll.TryGetValue(dll, out var c) ? c + 1 : 1;
            }
            foreach (var kv in byDll)
                pinv.Append($"{Csv(name)},{Csv(kv.Key)},{kv.Value}\n");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{name}] {ex.Message}");
        }
    }

    File.WriteAllText(outPrefix + "-refs.csv", refs.ToString());
    File.WriteAllText(outPrefix + "-pinvoke.csv", pinv.ToString());
    Console.WriteLine($"Scanned {scanned} managed assemblies.");
    Console.WriteLine($"Wrote {outPrefix}-refs.csv and {outPrefix}-pinvoke.csv");
    return 0;
}

// Windows rejects these in path segments; .NET Core's GetInvalidPathChars()
// returns only NUL, so it cannot be relied on here.
static string Sanitise(string s)
{
    var sb = new StringBuilder(s.Length);
    foreach (char c in s)
        sb.Append(c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' or '`' || c < 32 ? '_' : c);
    return sb.ToString().Trim('.', ' ');
}

// Keeps a path segment inside Windows' 255-char limit while staying stable and
// collision-free, by appending a hash of the part that was cut.
static string ShortenSegment(string s)
{
    if (s.Length <= 100) return s;
    uint h = 2166136261;
    foreach (char c in s) { h ^= c; h *= 16777619; }
    return s.Substring(0, 90) + "~" + h.ToString("X8");
}

static string Csv(string s)
{
    if (s is null) return "";
    s = s.Replace('\r', ' ').Replace('\n', ' ');
    return s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
