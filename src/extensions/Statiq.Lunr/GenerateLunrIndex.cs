﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Statiq.Common;

namespace Statiq.Lunr
{
    public class GenerateLunrIndex : Module
    {
        public static readonly NormalizedPath DefaultScriptPath = new NormalizedPath("search.js");
        public static readonly string DefaultClientName = "search";
        public static readonly string DefaultReferenceKey = "ref"; // Can't use "id" because IDocument.Id will override it

        // The keys in the search metadata objects to use for fields
        private readonly Dictionary<string, FieldType> _fieldKeys = new Dictionary<string, FieldType>()
        {
            { "link", FieldType.Result },
            { "title", FieldType.Searchable | FieldType.Result },
            { "content", FieldType.Searchable }
        };

        // Includes the host in the default link field
        private bool _includeHostInLink;

        // The key in the search metadata object to use for the ref
        private string _referenceKey = DefaultReferenceKey;

        // A search metadata object - only keys in _fieldKeys will be used, all others will be ignored
        private Config<IEnumerable<IEnumerable<KeyValuePair<string, object>>>> _getSearchItems;

        private NormalizedPath _scriptPath = DefaultScriptPath;

        private bool _zipIndexFile = true;

        // The destination path of the index file, will be "[_scriptDestinationPath].index.gz" or "[_scriptDestinationPath].index.json" if null
        private NormalizedPath _indexPath = NormalizedPath.Null;

        private bool _zipResultsFile = true;

        // The destination path of the results file, will be "[_scriptDestinationPath].results.json" if null
        private NormalizedPath _resultsPath = NormalizedPath.Null;

        private Func<StringBuilder, IExecutionContext, string> _customizeScript;

        private string _clientName = DefaultClientName;

        private string _searchItemsKey = LunrKeys.SearchItems;

        private bool _allowPositionMetadata;

        private Config<IEnumerable<string>> _getStopWords;

        private bool _removeHtml = true;

        /// <summary>
        /// Defines a search field and whether to include it in results.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The key corresponds to a key from the items returned from
        /// <see cref="WithSearchItems(Config{IEnumerable{IEnumerable{KeyValuePair{string, object}}}})"/>,
        /// or from the input documents by default.
        /// </para>
        /// <para>
        /// Including the field in the results (<see cref="FieldType.Result"/>) increases
        /// the size of the JSON file that contains result data, but allows using the value
        /// of the field from the client. Otherwise, just specifying <see cref="FieldType.Searchable"/>
        /// will allow searching the field value but not using it from the client. You can
        /// also specify both since <see cref="FieldType"/> is a <see cref="FlagsAttribute"/> enum.
        /// </para>
        /// </remarks>
        /// <param name="key">The key that holds the search value.</param>
        /// <param name="fieldType">The type of field.</param>
        /// <returns>The current module instance.</returns>
        public GenerateLunrIndex DefineField(string key, FieldType fieldType)
        {
            key.ThrowIfNullOrEmpty(nameof(key));
            _fieldKeys[key.ToLowerCamelCase()] = fieldType;
            return this;
        }

        /// <summary>
        /// Removes a field from the search index.
        /// </summary>
        /// <param name="key">The key to remove.</param>
        /// <returns>The current module instance.</returns>
        public GenerateLunrIndex RemoveField(string key)
        {
            key.ThrowIfNullOrEmpty(nameof(key));
            _fieldKeys.Remove(key.ToLowerCamelCase());
            return this;
        }

        /// <summary>
        /// Clears all fields from the search index.
        /// </summary>
        /// <returns>The current module instance.</returns>
        public GenerateLunrIndex ClearFields()
        {
            _fieldKeys.Clear();
            return this;
        }

        /// <summary>
        /// Indicates whether the host should be automatically included
        /// in generated links (the default is <c>false</c>).
        /// </summary>
        /// <param name="includeHostInLink"><c>true</c> to include the host.</param>
        /// <returns>The current module instance.</returns>
        public GenerateLunrIndex IncludeHostInLink(bool includeHostInLink = true)
        {
            _includeHostInLink = includeHostInLink;
            return this;
        }

        /// <summary>
        /// This allows you to customize the JavaScript file that this module creates.
        /// </summary>
        /// <param name="customizeScript">
        /// A script transformation function. The <see cref="StringBuilder"/> contains
        /// the generated script content. You can manipulate as appropriate and then return the final
        /// script as a <c>string</c>. If you return <c>null</c> then no script will be output (only
        /// the index file will be output).
        /// </param>
        /// <returns>The current module instance.</returns>
        public GenerateLunrIndex CustomizeScript(Func<StringBuilder, IExecutionContext, string> customizeScript)
        {
            _customizeScript = customizeScript;
            return this;
        }

        /// <summary>
        /// Controls the output path of the script file (by default the
        /// destination of the script file is "search.js").
        /// </summary>
        /// <param name="scriptPath">The script path.</param>
        /// <returns>The current module instance.</returns>
        public GenerateLunrIndex WithScriptPath(in NormalizedPath scriptPath)
        {
            _scriptPath = scriptPath.ThrowIfNull(nameof(scriptPath));
            return this;
        }

        /// <summary>
        /// Controls the output path of the search index file (by default the destination of the
        /// search index file is the same as the script file with a ".index.gz" extension, or
        /// ".index.json" extension if <see cref="ZipIndexFile(bool)"/> is <c>false</c>).
        /// or ).
        /// </summary>
        /// <param name="indexPath">The search index path.</param>
        /// <returns>The current module instance.</returns>
        public GenerateLunrIndex WithIndexPath(in NormalizedPath indexPath)
        {
            _indexPath = indexPath;
            return this;
        }

        /// <summary>
        /// Controls the output path of the results file that holds search field values as defined by <see cref="FieldType.Result"/>
        /// (by default the destination of the search index file is the same as the script file with a ".results.json" extension).
        /// </summary>
        /// <param name="resultsPath">The results file path.</param>
        /// <returns>The current module instance.</returns>
        public GenerateLunrIndex WithResultsPath(in NormalizedPath resultsPath)
        {
            _resultsPath = resultsPath;
            return this;
        }

        /// <summary>
        /// Use a custom delegate to get search items for each input document. Only the reference and search field keys
        /// will be retrieved from the returned item(s), all other values will be ignored (I.e. they won't be added to
        /// the search automatically).
        /// </summary>
        /// <param name="getSearchItems">A delegate that gets search items.</param>
        /// <returns>The current module instance.</returns>
        public GenerateLunrIndex WithSearchItems(Config<IEnumerable<IEnumerable<KeyValuePair<string, object>>>> getSearchItems)
        {
            _getSearchItems = getSearchItems;
            return this;
        }

        /// <summary>
        /// Sets an alternate reference key that will be used to get a unique identifier for each search metadata item.
        /// </summary>
        /// <param name="referenceKey">The reference key to use.</param>
        /// <returns>The current module instance.</returns>
        public GenerateLunrIndex WithReferenceKey(string referenceKey)
        {
            _referenceKey = referenceKey.ThrowIfNullOrEmpty(nameof(referenceKey));
            return this;
        }

        /// <summary>
        /// Sets a custom metadata key to use to get search items from each document if the document provides search items via metadata.
        /// </summary>
        /// <param name="searchItemsKey">The search items key to use.</param>
        /// <returns>The current module instance.</returns>
        public GenerateLunrIndex WithSearchItemsKey(string searchItemsKey)
        {
            _searchItemsKey = searchItemsKey.ThrowIfNullOrEmpty(nameof(searchItemsKey));
            return this;
        }

        /// <summary>
        /// Adds the "position" metadata to the metadata allowlist in the search index, which
        /// enables position information for each search term in the search results at the expense of index size.
        /// </summary>
        /// <remarks>
        /// See https://lunrjs.com/guides/core_concepts.html for more information.
        /// </remarks>
        /// <param name="allowPositionMetadata"><c>true</c> to add position metadata to the allowlist, <c>false</c> otherwise.</param>
        /// <returns>The current module instance.</returns>
        public GenerateLunrIndex AllowPositionMetadata(bool allowPositionMetadata = true)
        {
            _allowPositionMetadata = allowPositionMetadata;
            return this;
        }

        /// <summary>
        /// Specifies stops words to use for the search index. By default a pre-defined set of English stop words are used.
        /// </summary>
        /// <param name="getStopWords">
        /// A delegate that returns the stop words to use. Set to <c>null</c> to use the default English stop words.
        /// </param>
        /// <returns>The current module instance.</returns>
        public GenerateLunrIndex WithStopWords(Config<IEnumerable<string>> getStopWords)
        {
            _getStopWords = getStopWords.EnsureNonDocumentIfNonNull(nameof(getStopWords));
            return this;
        }

        /// <summary>
        /// Specifies an input file that contains stop words to use. The file should contain
        /// one stop word per line.
        /// </summary>
        /// <param name="stopWordsFilePath">The path to an input file that contains stop words.</param>
        /// <returns>The current module instance.</returns>
        public GenerateLunrIndex WithStopWordsFromFile(NormalizedPath stopWordsFilePath)
        {
            stopWordsFilePath.ThrowIfNull(nameof(stopWordsFilePath));
            _getStopWords = Config.FromContext<IEnumerable<string>>(async ctx =>
            {
                IFile stopWordsFile = ctx.FileSystem.GetInputFile(stopWordsFilePath);
                if (stopWordsFile.Exists)
                {
                    return (await stopWordsFile.ReadAllTextAsync())
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(f => f.Trim().ToLowerInvariant())
                        .Where(f => f.Length > 1)
                        .ToArray();
                }
                return null;
            });
            return this;
        }

        /// <summary>
        /// Indicates whether to gzip the index file (the default is <c>true</c>).
        /// </summary>
        /// <param name="zipIndexFile"><c>true</c> to gzip the index file, <c>false</c> otherwise.</param>
        /// <returns>The current module instance.</returns>
        public GenerateLunrIndex ZipIndexFile(bool zipIndexFile)
        {
            _zipIndexFile = zipIndexFile;
            return this;
        }

        /// <summary>
        /// Indicates whether to gzip the results file (the default is <c>true</c>).
        /// </summary>
        /// <param name="zipResultsFile"><c>true</c> to gzip the results file, <c>false</c> otherwise.</param>
        /// <returns>The current module instance.</returns>
        public GenerateLunrIndex ZipResultsFile(bool zipResultsFile)
        {
            _zipResultsFile = zipResultsFile;
            return this;
        }

        /// <summary>
        /// Indicates whether HTML should be removed from document content when the input document media
        /// type indicates the content is HTML (the default is <c>true</c>).
        /// </summary>
        /// <param name="removeHtml"><c>true</c> to remove HTML from the input document content, <c>false</c> otherwise.</param>
        /// <returns>The current module instance.</returns>
        public GenerateLunrIndex RemoveHtml(bool removeHtml)
        {
            _removeHtml = removeHtml;
            return this;
        }

        protected override async Task<IEnumerable<IDocument>> ExecuteContextAsync(IExecutionContext context)
        {
            Dictionary<string, Dictionary<string, object>> resultDictionaries = new Dictionary<string, Dictionary<string, object>>();
            Dictionary<string, Dictionary<string, object>> lazyDictionaries = new Dictionary<string, Dictionary<string, object>>();
            string camelCaseRefKey = _referenceKey.ToLowerCamelCase();
            bool addedFields = false;

            // Get stop words
            global::Lunr.StopWordFilterBase stopWordFilter = null;
            if (_getStopWords is object)
            {
                IEnumerable<string> stopWords = (await _getStopWords.GetValueAsync(null, context)) ?? Array.Empty<string>();
                stopWordFilter = new StopWordFilter(stopWords);
            }

            // Build the index
            global::Lunr.Index searchIndex = await global::Lunr.Index.Build(
                async indexBuilder =>
                {
                    // Iterate the input documents
                    foreach (IDocument input in context.Inputs)
                    {
                        // Omit documents that shouldn't be processed
                        if (!input.GetBool(LunrKeys.OmitFromSearch))
                        {
                            IEnumerable<IEnumerable<KeyValuePair<string, object>>> searchItems = _getSearchItems is object
                                ? await _getSearchItems.GetValueAsync(input, context)
                                : await DefaultGetSearchItemsAsync(input);
                            if (searchItems is object)
                            {
                                foreach (IEnumerable<KeyValuePair<string, object>> searchItem in searchItems)
                                {
                                    if (searchItem is object)
                                    {
                                        // Clone the original input document so any additional metadata it contains can be added to the field keys without problems
                                        IDocument searchDocument = input.Clone(searchItem);

                                        // Create the search document and data dictionaries
                                        global::Lunr.Document lunrDocument = new global::Lunr.Document();
                                        Dictionary<string, object> resultDictionary = new Dictionary<string, object>();

                                        // Get the reference value and only add a search item if we have one
                                        string refValue = searchDocument.GetString(_referenceKey);
                                        if (!refValue.IsNullOrEmpty())
                                        {
                                            // Add the reference value
                                            // We only need to add it to the search document, the eager document object is keyed by reference value and the lazy file name is the reference value
                                            lunrDocument.Add(camelCaseRefKey, refValue);

                                            // Iterate fields and populate the search document and data dictionaries
                                            bool hasResultField = false;
                                            foreach (KeyValuePair<string, FieldType> fieldKey in _fieldKeys.OrderBy(x => x.Key))
                                            {
                                                // Convert to either an array of strings or a single string
                                                object searchValue = searchDocument.Get(fieldKey.Key);
                                                if (!(searchValue is IEnumerable<string>))
                                                {
                                                    searchValue = TypeHelper.Convert<string>(searchValue);
                                                }
                                                if (searchValue is object)
                                                {
                                                    // Add to the search document
                                                    if (fieldKey.Value.HasFlag(FieldType.Searchable))
                                                    {
                                                        lunrDocument.Add(fieldKey.Key, searchValue);
                                                    }

                                                    // Add to the results dictionaries
                                                    if (fieldKey.Value.HasFlag(FieldType.Result))
                                                    {
                                                        resultDictionary.Add(fieldKey.Key, searchValue);
                                                        hasResultField = true;
                                                    }
                                                }
                                            }

                                            // Add the fields if this is the first document (only add them when we know we have at least one document,
                                            // otherwise the search index build throws for an empty document set)
                                            if (!addedFields)
                                            {
                                                // Add the reference field
                                                indexBuilder.ReferenceField = camelCaseRefKey;

                                                // Enable position metadata
                                                if (_allowPositionMetadata)
                                                {
                                                    indexBuilder.MetadataAllowList.Add("position");
                                                }

                                                // Add search fields
                                                foreach (KeyValuePair<string, FieldType> fieldKey in _fieldKeys.Where(x => x.Value.HasFlag(FieldType.Searchable)))
                                                {
                                                    indexBuilder.AddField(fieldKey.Key);
                                                }
                                                addedFields = true;
                                            }

                                            // Add the search document and data dictionaries
                                            await indexBuilder.Add(lunrDocument, cancellationToken: context.CancellationToken);
                                            if (hasResultField)
                                            {
                                                resultDictionaries.Add(refValue, resultDictionary);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                },
                stopWordFilter: stopWordFilter);

            // Create output documents
            List<IDocument> outputs = new List<IDocument>();

            // Output the index in a separate document
            string indexJson = searchIndex.ToJson();
            NormalizedPath indexPath = await AddJsonOutputAsync(outputs, _zipIndexFile, "index", _indexPath, indexJson, context);

            // Output a result file (if we have any results)
            NormalizedPath resultsPath = NormalizedPath.Null;
            if (resultDictionaries.Count > 0)
            {
                string resultsJson = System.Text.Json.JsonSerializer.Serialize(resultDictionaries);
                resultsPath = await AddJsonOutputAsync(outputs, _zipResultsFile, "results", _resultsPath, resultsJson, context);
            }

            // Build the search JavaScript file, allowing for overriding the output
            StringBuilder scriptBuilder = new StringBuilder($@"const {_clientName} {{
    indexFile: '{context.GetLink(indexPath)}'");
            if (!resultsPath.IsNull)
            {
                scriptBuilder.Append($@",
    resultsFile: '{context.GetLink(resultsPath)}'");
            }
            scriptBuilder.Append(@"
}};");
            string script = _customizeScript is object ? _customizeScript.Invoke(scriptBuilder, context) : scriptBuilder.ToString();
            if (!script.IsNullOrEmpty())
            {
                // Only output the script if it wasn't overridden to null or empty
                outputs.Add(context.CreateDocument(_scriptPath, context.GetContentProvider(script, MediaTypes.JavaScript)));
            }

            // TODO: Change the script output for loading the index and results files depending on if it's zipped or not

            return outputs;
        }

        private async Task<NormalizedPath> AddJsonOutputAsync(
            List<IDocument> outputs,
            bool zipFile,
            string fileName,
            NormalizedPath pathOverride,
            string jsonContent,
            IExecutionContext context)
        {
            if (zipFile)
            {
                // Zip the results file
                NormalizedPath zipPath = pathOverride.IsNullOrEmpty ? _scriptPath.ChangeExtension($".{fileName}.gz") : pathOverride;
                byte[] contentBytes = Encoding.UTF8.GetBytes(jsonContent);
                using (Stream contentStream = context.GetContentStream())
                {
                    using (GZipStream zipStream = new GZipStream(contentStream, CompressionLevel.Optimal))
                    {
                        await zipStream.WriteAsync(contentBytes, context.CancellationToken);
                    }
                    outputs.Add(context.CreateDocument(zipPath, context.GetContentProvider(contentStream, MediaTypes.Get(".gz"))));
                }
                return zipPath;
            }

            // No zipping needed
            NormalizedPath outputPath = pathOverride.IsNullOrEmpty ? _scriptPath.ChangeExtension($".{fileName}.json") : pathOverride;
            outputs.Add(context.CreateDocument(outputPath, context.GetContentProvider(jsonContent, MediaTypes.Json)));
            return outputPath;
        }

        private async Task<IEnumerable<IEnumerable<KeyValuePair<string, object>>>> DefaultGetSearchItemsAsync(IDocument input)
        {
            // Try to get search items from the metadata key
            IEnumerable<IEnumerable<KeyValuePair<string, object>>> searchItems = input.GetList<IEnumerable<KeyValuePair<string, object>>>(_searchItemsKey);
            if (searchItems is object)
            {
                return searchItems;
            }

            // Get the content of this document and remove the HTML if requested
            string content = await input.GetContentStringAsync();
            if (_removeHtml && (input.MediaTypeEquals(MediaTypes.Html) || input.MediaTypeEquals(MediaTypes.HtmlFragment)))
            {
                content = content.RemoveHtmlAndSpecialChars();
            }

            // Get the default search metadata for this input document
            Dictionary<string, object> searchItem = new Dictionary<string, object>
            {
                { "link", input.GetLink(_includeHostInLink) },
                { "title", input.GetTitle() },
                { "content", content }
            };
            if (input.GetString(_referenceKey) is null)
            {
                searchItem.Add(_referenceKey, await input.GetCacheCodeAsync());
            }
            return new IEnumerable<KeyValuePair<string, object>>[] { searchItem };
        }
    }
}
