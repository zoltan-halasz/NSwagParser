using Newtonsoft.Json.Linq;
using NJsonSchema;
using NJsonSchema.CodeGeneration.TypeScript;
using NSwag;
using NSwag.CodeGeneration.TypeScript;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NSwagParser
{
    class Program
    {
        static async Task Main(string[] args)
        {
            string swaggerUrl = "https://ncangular.coraxwms.nl/swagger/docs/v1";
            string outputDirectory = "C:\\Users\\zoltan.halasz\\NC-CoraxWeb\\src\\api";
    
            try
            {
                await GenerateTypeScriptByNamespace(swaggerUrl, outputDirectory);
                Console.WriteLine("TypeScript generation completed successfully!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }

        static async Task GenerateTypeScriptByNamespace(string swaggerFilePath, string outputDirectory)
        {
            // Ensure output directory exists
            Directory.CreateDirectory(outputDirectory);

            // Read and parse the swagger document            
            var document = await OpenApiDocument.FromUrlAsync(swaggerFilePath);

            // Group definitions by namespace
            var namespaceGroups = GroupDefinitionsByNamespace(document);

            foreach (var namespaceGroup in namespaceGroups)
            {
                string namespaceName = namespaceGroup.Key;
                var definitions = namespaceGroup.Value;

                Console.WriteLine($"Processing namespace: {namespaceName} ({definitions.Count} definitions)");

                // Create a mini document for this namespace
                var miniDocument = CreateMiniDocumentForNamespace(document, definitions);

                // Generate TypeScript code for this namespace
                string typeScriptCode = GenerateTypeScriptForNamespace(miniDocument, namespaceName);

                // Write to file
                string namespaceFileName = SanitizeFileName(namespaceName);
                string outputPath = Path.Combine(outputDirectory, $"{namespaceFileName}.ts");

                await File.WriteAllTextAsync(outputPath, typeScriptCode);
                Console.WriteLine($"Generated: {outputPath}");
            }
        }

        static Dictionary<string, List<KeyValuePair<string, JsonSchema>>> GroupDefinitionsByNamespace(OpenApiDocument document)
        {
            var namespaceGroups = new Dictionary<string, List<KeyValuePair<string, JsonSchema>>>();

            if (document.Definitions == null)
            {
                Console.WriteLine("Warning: No definitions found in swagger document.");
                return namespaceGroups;
            }

            foreach (var definition in document.Definitions)
            {
                string fullTypeName = definition.Key;
                string namespaceName = ExtractNamespace(fullTypeName);

                if (!namespaceGroups.ContainsKey(namespaceName))
                {
                    namespaceGroups[namespaceName] = new List<KeyValuePair<string, JsonSchema>>();
                }

                namespaceGroups[namespaceName].Add(definition);
            }

            return namespaceGroups;
        }

        static string ExtractNamespace(string fullTypeName)
        {
            // Extract namespace from full type name
            // e.g., "Corax.Core.Inbound.Commands.ManageReceiptLinesCommand" -> "Corax.Core.Inbound.Commands"
            var parts = fullTypeName.Split('.');
            if (parts.Length <= 1)
                return "Global"; // Default namespace for types without namespace

            // Take all parts except the last one (which is the class name)
            return string.Join(".", parts.Take(parts.Length - 1));
        }

        static OpenApiDocument CreateMiniDocumentForNamespace(OpenApiDocument originalDocument, List<KeyValuePair<string, JsonSchema>> definitions)
        {
            // Create a new document with only the definitions for this namespace
            var miniDocument = new OpenApiDocument
            {
                Info = new OpenApiInfo
                {
                    Title = originalDocument.Info?.Title ?? "Generated API",
                    Version = originalDocument.Info?.Version ?? "1.0.0"
                },                
            };

            // Add the definitions for this namespace
            foreach (var definition in definitions)
            {
                miniDocument.Definitions[definition.Key] = definition.Value;
            }

            // Find and add referenced definitions recursively
            AddReferencedDefinitions(miniDocument, originalDocument, new HashSet<string>());

            return miniDocument;
        }

        static void AddReferencedDefinitions(OpenApiDocument miniDocument, OpenApiDocument originalDocument, HashSet<string> visited)
        {
            bool foundNewReferences = false;
            var referencesToAdd = new List<string>();

            // Scan existing definitions for references
            foreach (var definition in miniDocument.Definitions.Values)
            {
                CollectReferences(definition, referencesToAdd, visited);
            }

            // Add referenced definitions that aren't already included
            foreach (string reference in referencesToAdd)
            {
                if (!miniDocument.Definitions.ContainsKey(reference) && originalDocument.Definitions.ContainsKey(reference))
                {
                    miniDocument.Definitions.Add(reference, originalDocument.Definitions[reference]);
                    foundNewReferences = true;
                }
            }

            // Recursively add references if we found new ones
            if (foundNewReferences)
            {
                AddReferencedDefinitions(miniDocument, originalDocument, visited);
            }
        }

        static void CollectReferences(JsonSchema schema, List<string> references, HashSet<string> visited)
        {
            if (schema == null)
                return;

            // Avoid infinite recursion
            string schemaKey = schema.GetHashCode().ToString();
            if (visited.Contains(schemaKey))
                return;
            visited.Add(schemaKey);

            // Check for direct reference
            if (schema.Reference is not null)
            {
                string refName = ExtractReferenceTypeName(schema.Reference?.Title);
                if (!string.IsNullOrEmpty(refName))
                {
                    references.Add(refName);
                }
            }

            // Check properties
            if (schema.Properties != null)
            {
                foreach (var property in schema.Properties.Values)
                {
                    CollectReferences(property, references, visited);
                }
            }

            // Check array items
            if (schema.Item != null)
            {
                CollectReferences(schema.Item, references, visited);
            }

            // Check additional properties
            if (schema.AdditionalPropertiesSchema != null)
            {
                CollectReferences(schema.AdditionalPropertiesSchema, references, visited);
            }

            // Check all of schemas (for inheritance)
            if (schema.AllOf != null)
            {
                foreach (var allOfSchema in schema.AllOf)
                {
                    CollectReferences(allOfSchema, references, visited);
                }
            }

            // Check any of schemas
            if (schema.AnyOf != null)
            {
                foreach (var anyOfSchema in schema.AnyOf)
                {
                    CollectReferences(anyOfSchema, references, visited);
                }
            }

            // Check one of schemas
            if (schema.OneOf != null)
            {
                foreach (var oneOfSchema in schema.OneOf)
                {
                    CollectReferences(oneOfSchema, references, visited);
                }
            }
        }

        static string ExtractReferenceTypeName(string reference)
        {
            // Extract type name from reference like "#/definitions/Corax.Core.Inbound.Application.Receipts.RegisterReceiptLineModel"
            if (reference is not null && reference.StartsWith("#/definitions/"))
            {
                return reference.Substring("#/definitions/".Length);
            }
            return null;
        }

        static string GenerateTypeScriptForNamespace(OpenApiDocument miniDocument, string namespaceName)
        {
            string namespaceFileName = SanitizeFileName(namespaceName);

            // Configure TypeScript generation settings
            var settings = new TypeScriptClientGeneratorSettings()
            {
                ClassName = $"{namespaceFileName}Client",
                TypeScriptGeneratorSettings = {
                    GenerateConstructorInterface = false,
                    NullValue = TypeScriptNullValue.Null,
                    TypeStyle = TypeScriptTypeStyle.Interface,
                    DateTimeType = TypeScriptDateTimeType.Date,
                    GenerateCloneMethod = false,
                    ExtendedClasses = Array.Empty<string>(),
                    ExcludedTypeNames = Array.Empty<string>()
                }
            };

            // Generate TypeScript code
            var generator = new TypeScriptClientGenerator(miniDocument, settings);
            var code = generator.GenerateFile();

            return code;
        }

        static string SanitizeFileName(string fileName)
        {
            // Replace invalid file name characters with underscores
            var invalidChars = Path.GetInvalidFileNameChars();
            foreach (char invalidChar in invalidChars)
            {
                fileName = fileName.Replace(invalidChar, '_');
            }

            // Also replace dots with underscores for cleaner file names           

            return fileName;
        }
    }
}