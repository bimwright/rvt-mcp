using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.CodeAnalysis;

namespace RvtMcp.Plugin
{
    internal sealed class SkippedScriptReference
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// Turns assembly paths into Roslyn references, skipping files whose metadata Roslyn cannot read
    /// (issue #38: protected add-in DLLs that the CLR loads but System.Reflection.Metadata rejects with CS0009).
    /// </summary>
    internal static class ScriptReferences
    {
        internal static MetadataReference[] FromPaths(IEnumerable<string> paths, out SkippedScriptReference[] skipped)
        {
            var references = new List<MetadataReference>();
            var skips = new List<SkippedScriptReference>();
            foreach (var path in paths)
            {
                try
                {
                    references.Add(Load(path));
                }
                catch (Exception ex) when (ex is BadImageFormatException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    skips.Add(new SkippedScriptReference
                    {
                        Name = System.IO.Path.GetFileNameWithoutExtension(path),
                        Path = path,
                        Reason = ex.Message
                    });
                }
            }
            skipped = skips.ToArray();
            return references.ToArray();
        }

        private static MetadataReference Load(string path)
        {
            var metadata = AssemblyMetadata.CreateFromFile(path);
            try
            {
                // Read what Roslyn reads eagerly when it opens a reference: the assembly identity and the
                // identities of its assembly references. Method/field signatures are read lazily and do not break compiles.
                foreach (var module in metadata.GetModules())
                {
                    var reader = module.GetMetadataReader();
                    if (reader.IsAssembly)
                    {
                        var definition = reader.GetAssemblyDefinition();
                        reader.GetString(definition.Name);
                        reader.GetBlobBytes(definition.PublicKey);
                    }
                    foreach (var handle in reader.AssemblyReferences)
                    {
                        var reference = reader.GetAssemblyReference(handle);
                        reader.GetString(reference.Name);
                        reader.GetBlobBytes(reference.PublicKeyOrToken);
                    }
                }
                return metadata.GetReference(filePath: path);
            }
            catch
            {
                metadata.Dispose();
                throw;
            }
        }
    }
}
