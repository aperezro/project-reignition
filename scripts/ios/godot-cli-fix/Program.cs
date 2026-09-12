using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 2) throw new ArgumentException("Expected GodotTools.dll and local dotnet executable");
string dll = Path.GetFullPath(args[0]);
string cli = Path.GetFullPath(args[1]);
if (!File.Exists(cli)) throw new FileNotFoundException(cli);
string backup = dll + ".original";
if (!File.Exists(backup)) File.Copy(dll, backup);
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(dll));
resolver.AddSearchDirectory(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(dll)!, "../Api/Debug")));
using var module = ModuleDefinition.ReadModule(backup, new ReaderParameters { AssemblyResolver = resolver });
var finder = module.Types.Single(t => t.FullName == "GodotTools.Build.DotNetFinder");
var method = finder.Methods.Single(m => m.Name == "FindDotNetExe");
int replacements = 0;
foreach (var instruction in method.Body.Instructions)
{
    if (instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string value &&
        value is "/usr/local/share/dotnet/dotnet" or "/usr/local/share/dotnet/x64/dotnet")
    {
        instruction.Operand = cli;
        replacements++;
    }
}
if (replacements != 2) throw new InvalidOperationException($"Unexpected Godot 4.7 CLI resolver: {replacements}");
module.Write(dll + ".patched");
File.Move(dll + ".patched", dll, true);
Console.WriteLine($"Godot export CLI now uses {cli}; original kept at {backup}");
