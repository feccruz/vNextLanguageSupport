﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Framework.Runtime;

namespace CscSupport
{
    public class CscProjectReference : IMetadataProjectReference
    {
        private readonly string _configuration;
        private readonly Project _project;
        private readonly ILibraryExport _projectExport;
        private readonly FrameworkName _targetFramework;

        public CscProjectReference(Project project, FrameworkName targetFramework, string configuration, ILibraryExport projectExport)
        {
            _project = project;
            _targetFramework = targetFramework;
            _configuration = configuration;
            _projectExport = projectExport;
        }

        public string Name
        {
            get
            {
                return _project.Name;
            }
        }

        public string ProjectPath
        {
            get
            {
                return _project.ProjectFilePath;
            }
        }

        public IProjectBuildResult EmitAssembly(string outputPath)
        {
            return Emit(outputPath, emitPdb: true, emitDocFile: true);
        }

        public IProjectBuildResult EmitAssembly(Stream assemblyStream, Stream pdbStream)
        {
            string outputDir = Path.Combine(Path.GetTempPath(), "dynamic-assemblies");

            var result = Emit(outputDir, emitPdb: true, emitDocFile: false);

            if (!result.Success)
            {
                return result;
            }

            var assemblyPath = Path.Combine(outputDir, _project.Name + ".dll");
            var pdbPath = Path.Combine(outputDir, _project.Name + ".pdb");

            using (var afs = File.OpenRead(assemblyPath))
            {
                afs.CopyToAsync(assemblyStream);
            }

            using (var pdbfs = File.OpenRead(pdbPath))
            {
                pdbfs.CopyToAsync(pdbStream);
            }

            return result;
        }

        public void EmitReferenceAssembly(Stream stream)
        {
            string outputDir = Path.Combine(Path.GetTempPath(), "reference-assembly-" + Guid.NewGuid().ToString());

            try
            {
                var result = Emit(outputDir, emitPdb: false, emitDocFile: false);

                if (!result.Success)
                {
                    return;
                }

                using (var fs = File.OpenRead(Path.Combine(outputDir, _project.Name + ".dll")))
                {
                    fs.CopyToAsync(stream);
                }
            }
            finally
            {
                Directory.Delete(outputDir);
            }
        }

        public IProjectBuildResult GetDiagnostics()
        {
            string outputDir = Path.Combine(Path.GetTempPath(), "diagnostics-" + Guid.NewGuid().ToString());

            try
            {
                return Emit(Path.GetTempPath(), emitPdb: false, emitDocFile: false);
            }
            finally
            {
                Directory.Delete(outputDir);
            }
        }

        public IList<ISourceReference> GetSources()
        {
            return _project.SourceFiles.Select(p => (ISourceReference)new SourceFileReference(p)).ToList();
        }

        public IProjectBuildResult Emit(string outputPath, bool emitPdb, bool emitDocFile)
        {
            var tempBasePath = Path.Combine(outputPath, _project.Name, "obj");
            var outputDll = Path.Combine(outputPath, _project.Name + ".dll");

            // csc /out:foo.dll / target:library Program.cs
            var cscArgBuilder = new StringBuilder()
                    .AppendFormat(@"/out:""{0}"" ", outputDll)
                    .Append("/target:library ")
                    .Append("/noconfig ")
                    .Append("/nostdlib ");

            Directory.CreateDirectory(tempBasePath);

            if (emitPdb)
            {
                var pdb = Path.Combine(outputPath, _project.Name + ".pdb");

                cscArgBuilder = cscArgBuilder
                    .Append("/debug ")
                    .AppendFormat(@"/pdb:""{0}"" ", pdb);
            }

            if (emitDocFile)
            {
                var doc = Path.Combine(outputPath, _project.Name + ".xml");

                cscArgBuilder = cscArgBuilder
                    .AppendFormat(@"/doc:""{0}"" ", doc);
            }

            var cscArgs = cscArgBuilder
                    .Append(string.Join(" ", _project.SourceFiles.Select(s => "\"" + s + "\"")))
                    .Append(" ");

            var tempFiles = new List<string>();

            // These are the metadata references being used by your project.
            // Everything in your project.json is resolved and normailzed here:
            // - Project references
            // - Package references are turned into the appropriate assemblies
            // - Assembly neutral references
            // Each IMetadaReference maps to an assembly
            foreach (var reference in _projectExport.MetadataReferences)
            {
                // Skip this project
                if (reference.Name == typeof(CscProjectReference).Assembly.GetName().Name)
                {
                    continue;
                }

                // NuGet references
                var fileReference = reference as IMetadataFileReference;
                if (fileReference != null)
                {
                    cscArgs.AppendFormat(@"/r:""{0}""", fileReference.Path)
                      .Append(" ");
                }

                // Assembly neutral references
                var embeddedReference = reference as IMetadataEmbeddedReference;
                if (embeddedReference != null)
                {
                    var tempEmbeddedPath = Path.Combine(tempBasePath, reference.Name + ".dll");

                    // Write the ANI to disk for csc
                    File.WriteAllBytes(tempEmbeddedPath, embeddedReference.Contents);

                    cscArgs.AppendFormat(@"/r:""{0}""", tempEmbeddedPath)
                      .Append(" ");

                    tempFiles.Add(tempEmbeddedPath);
                }

                var projectReference = reference as IMetadataProjectReference;
                if (projectReference != null)
                {
                    // You can write the reference assembly to the stream
                    // and add the reference to your compiler

                    var tempProjectDll = Path.Combine(tempBasePath, reference.Name + ".dll");

                    using (var fs = File.OpenWrite(tempProjectDll))
                    {
                        projectReference.EmitReferenceAssembly(fs);
                    }

                    cscArgs.AppendFormat(@"/r:""{0}""", tempProjectDll)
                      .Append(" ");

                    tempFiles.Add(tempProjectDll);
                }
            }

            Console.WriteLine(cscArgs.ToString());

            var si = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\Framework\v4.0.30319\csc.exe"),
                Arguments = cscArgs.ToString(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var process = Process.Start(si);
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                // TODO: Parse errors from std(err/out)
                return new ProjectBuildResult(success: false, warnings: new string[0], errors: new[] { "Compilation failed" });
            }

            // Nuke the temporary references on disk
            tempFiles.ForEach(File.Delete);

            Directory.Delete(tempBasePath);

            return new ProjectBuildResult(success: true, warnings: new string[0], errors: new string[0]);
        }
    }
}