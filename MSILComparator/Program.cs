using CliWrap;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using System.CommandLine;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ChildCommand = System.CommandLine.Command;
using Command = CliWrap.Command;

namespace MSILComparator
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Argument<IEnumerable<string>> argument = new("dll-directories|dll-file", description: "程序集或程序集目录");
            Option<string?> outputDirectory = new(["--output-directory", "-o"], description: "输出IL文件目录");
            Option<bool> keepDirStruct = new(["--keep-directory-struct", "-k"], () => true, description: "保持目录结构");
            Option<bool> useILSpyCover = new(["--use-ilspy-cover", "-c"], () => true, description: "使用ICSharpCode.Decompiler覆盖IL文件，解决成员顺序不一致，详见 https://github.com/icsharpcode/ILSpy");
            Option<string> searchPattern = new("--search-Pattern", () => "*", description: "程序集搜索字符串");
            ChildCommand il = new("il", "输出MSIL");
            il.AddArgument(argument);
            il.AddOption(outputDirectory);
            il.AddOption(searchPattern);
            il.AddOption(keepDirStruct);
            il.AddOption(useILSpyCover);
            il.SetHandler(ILDisassembler, argument, outputDirectory, searchPattern, keepDirStruct, useILSpyCover);

            RootCommand rootCommand = new("程序集MSIL比较工具");
            rootCommand.AddCommand(il);

            await rootCommand.InvokeAsync(args);
        }

        public static async Task ILDisassembler(IEnumerable<string> dllFileOrDirectories, string? outputDirectory, string searchPattern, bool keepDirStruct, bool useILSpy)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                outputDirectory = Environment.CurrentDirectory;
            }

            foreach (string dllFileOrDirectory in dllFileOrDirectories)
            {
                List<string> dllFiles = [];
                if (File.Exists(dllFileOrDirectory))
                {
                    if (IsAssembly(dllFileOrDirectory))
                    {
                        dllFiles.Add(dllFileOrDirectory);
                    }
                }
                else if (Directory.Exists(dllFileOrDirectory))
                {
                    foreach (var dllfiles in Directory.GetFiles(dllFileOrDirectory, searchPattern, SearchOption.AllDirectories))
                    {
                        if (IsAssembly(dllfiles))
                        {
                            dllFiles.Add(dllfiles);
                        }
                    }
                }
                else
                {
                    throw new DirectoryNotFoundException($"The {dllFileOrDirectory} file or directory cannot be found.");
                }

                foreach (var dllFile in dllFiles)
                {
                    if (!IsAssembly(dllFile))
                    {
                        continue;
                    }

                    string outputILFile;
                    string outputILFileName = Path.GetFileName(dllFile) + ".il";
                    string outputILDirectory = Path.Combine(outputDirectory, new DirectoryInfo(dllFileOrDirectory).Name) + ".il";
                    if (!File.GetAttributes(dllFileOrDirectory).HasFlag(FileAttributes.Directory))
                    {
                        // output/ilfile
                        outputILFile = Path.Combine(outputILDirectory, outputILFileName);
                    }
                    else if (keepDirStruct)
                    {
                        // output/relative/dllname/ilfile
                        string relativePath = Path.GetRelativePath(dllFileOrDirectory, Path.GetDirectoryName(dllFile)!);
                        outputILFile = Path.Combine(outputILDirectory, relativePath, Path.GetFileName(dllFile), outputILFileName);
                    }
                    else
                    {
                        // output/dllname/ilfile
                        outputILFile = Path.Combine(outputILDirectory, Path.GetFileName(dllFile), outputILFileName);
                    }

                    string outputDirctory = Path.GetDirectoryName(outputILFile)!;
                    if (!Directory.Exists(outputDirctory))
                    {
                        Directory.CreateDirectory(outputDirctory);
                    }

                    await DisassembleByILDASMAsync(dllFile, outputILFile);
                    if (useILSpy)
                    {
                        DisassembleByILSpy(dllFile, outputILFile);
                    }

                    await Console.Out.WriteLineAsync($"ildasm {dllFile} to {outputILFile}");
                }
            }
        }

        public static async Task DisassembleByILDASMAsync(string sourceFileName, string outputFile)
        {
            const string ILDASM = "ildasm.exe";

            string ildasmPath = Path.Combine(Environment.CurrentDirectory, ILDASM);
            if (!Path.Exists(ildasmPath))
            {
                throw new FileNotFoundException($"The {ILDASM} file cannot be found.", ILDASM);
            }

            Command command = Cli.Wrap(ILDASM)
                .WithArguments($""" "{sourceFileName}" /all /out="{outputFile}" """)
                .WithValidation(CommandResultValidation.None);
            await command.ExecuteAsync();
        }

        public static void DisassembleByILSpy(string sourceFileName, string outputFile)
        {
            using var peFileStream = new FileStream(sourceFileName, FileMode.Open, FileAccess.Read);
            using var peFile = new PEFile(sourceFileName, peFileStream);
            using var writer = new StringWriter();

            MetadataReader metadata = peFile.Metadata;
            PlainTextOutput output = new(writer);
            ReflectionDisassembler rd = new(output, CancellationToken.None)
            {
                EntityProcessor = new SortByNameProcessor(),
                AssemblyResolver = new UniversalAssemblyResolver(sourceFileName, throwOnError: true, null),
            };

            rd.WriteAssemblyReferences(metadata);
            if (metadata.IsAssembly)
            {
                rd.WriteAssemblyHeader(peFile);
            }

            output.WriteLine();
            rd.WriteModuleHeader(peFile, skipMVID: true);
            output.WriteLine();
            rd.WriteModuleContents(peFile);

            File.WriteAllText(outputFile, writer.ToString());
        }

        private static bool IsAssembly(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                // Try to read CLI metadata from the PE file.
                using var peReader = new PEReader(fs);

                if (!peReader.HasMetadata)
                {
                    return false; // File does not have CLI metadata.
                }

                // Check that file has an assembly manifest.
                MetadataReader reader = peReader.GetMetadataReader();
                return reader.IsAssembly;
            }
            catch (BadImageFormatException)
            {
                Console.WriteLine($"The {path} file is not an executable.");
            }
            catch (FileNotFoundException)
            {
                Console.WriteLine($"The {path} file cannot be found.");
            }

            return false;
        }
    }
}
