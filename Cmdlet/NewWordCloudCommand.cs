﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Management.Automation;
using System.Threading.Tasks;
using SkiaSharp;

namespace PSWordCloud
{
    [Cmdlet(VerbsCommon.New, "WordCloud", SupportsShouldProcess = true, DefaultParameterSetName = "ColorBackground")]
    public class NewWordCloudCommand : PSCmdlet
    {
        private const float FOCUS_WORD_SCALE = 1.3f;

        #region Parameters

        [Parameter(Mandatory = true, ValueFromPipeline = true, ParameterSetName = "ColorBackground")]
        [Parameter(Mandatory = true, ValueFromPipeline = true, ParameterSetName = "ColorBackground-Mono")]
        [Parameter(Mandatory = true, ValueFromPipeline = true, ParameterSetName = "FileBackground")]
        [Parameter(Mandatory = true, ValueFromPipeline = true, ParameterSetName = "FileBackground-Mono")]
        [Alias("InputString", "Text", "String", "Words", "Document", "Page")]
        [AllowEmptyString()]
        public PSObject InputObject { get; set; }

        private string[] _resolvedPaths;
        [Parameter(Mandatory = true, Position = 0, ParameterSetName = "ColorBackground")]
        [Parameter(Mandatory = true, Position = 0, ParameterSetName = "ColorBackground-Mono")]
        [Parameter(Mandatory = true, Position = 0, ParameterSetName = "FileBackground")]
        [Parameter(Mandatory = true, Position = 0, ParameterSetName = "FileBackground-Mono")]
        [Alias("OutFile", "ExportPath", "ImagePath")]
        public string[] Path { get; set; }

        [Parameter]
        [Alias("Title")]
        public string FocusWord { get; set; }

        [Parameter]
        [Alias("MaxWords")]
        [ValidateRange(0, 1000)]
        public ushort MaxRenderedWords { get; set; } = 100;

        #endregion Parameters

        private List<string> _inputCache = new List<string>(256);
        private List<Task<string[]>> _taskCache = new List<Task<string[]>>();
        private static readonly string[] _stopWords = new[] {
            "a","about","above","after","again","against","all","am","an","and","any","are","aren't","as","at","be",
            "because","been","before","being","below","between","both","but","by","can't","cannot","could","couldn't",
            "did","didn't","do","does","doesn't","doing","don't","down","during","each","few","for","from","further",
            "had","hadn't","has","hasn't","have","haven't","having","he","he'd","he'll","he's","her","here","here's",
            "hers","herself","him","himself","his","how","how's","i","i'd","i'll","i'm","i've","if","in","into","is",
            "isn't","it","it's","its","itself","let's","me","more","most","mustn't","my","myself","no","nor","not","of",
            "off","on","once","only","or","other","ought","our","ours","ourselves","out","over","own","same","shan't",
            "she","she'd","she'll","she's","should","shouldn't","so","some","such","than","that","that's","the","their",
            "theirs","them","themselves","then","there","there's","these","they","they'd","they'll","they're","they've",
            "this","those","through","to","too","under","until","up","very","was","wasn't","we","we'd","we'll","we're",
            "we've","were","weren't","what","what's","when","when's","where","where's","which","while","who","who's",
            "whom","why","why's","with","won't","would","wouldn't","you","you'd","you'll","you're","you've","your",
            "yours","yourself","yourselves",
        };

        private static readonly char[] _splitChars = new[] {
            ' ','.',',','"','?','!','{','}','[',']',':','(',')','“','”','*','#','%','^','&','+','='
        };

        private List<Task<string[]>> _wordProcessingTasks;

        protected override void BeginProcessing()
        {
            var targetPaths = new List<string>();

            foreach (string path in Path)
            {
                var resolvedPaths = SessionState.Path.GetResolvedProviderPathFromPSPath(path, out ProviderInfo provider);
                if (resolvedPaths != null)
                {
                    targetPaths.AddRange(resolvedPaths);
                }
            }

            _resolvedPaths = targetPaths.ToArray();
        }

        protected override void ProcessRecord()
        {
            string[] text;
            if (MyInvocation.ExpectingInput)
            {
                text = new[] { InputObject.BaseObject as string };
            }
            else
            {
                text = InputObject.BaseObject as string[];
                if (text == null)
                {
                    text = new[] { InputObject.BaseObject as string };
                }
            }

            if (_wordProcessingTasks == null)
            {
                _wordProcessingTasks = new List<Task<string[]>>(text.Length);
            }

            foreach (var line in text)
            {
                _wordProcessingTasks.Add(Task.Run(async () => await ProcessLineAsync(line)));
            }
        }

        protected override void EndProcessing()
        {
            Dictionary<string, float> wordEmSizeDictionary = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var lineStrings = Task.WhenAll<string[]>(_wordProcessingTasks);
            var countJobs = new List<Task>();
            lineStrings.Wait();
            foreach (var lineWords in lineStrings.Result)
            {
                foreach (string word in lineWords)
                {
                    var trimmedWord = word.TrimEnd('s');
                    var pluralWord = String.Format("{0}s", word);
                    if (wordEmSizeDictionary.ContainsKey(trimmedWord))
                    {
                        wordEmSizeDictionary[trimmedWord]++;
                    }
                    else if (wordEmSizeDictionary.ContainsKey(pluralWord))
                    {
                        wordEmSizeDictionary[word] = wordEmSizeDictionary[pluralWord] + 1;
                        wordEmSizeDictionary.Remove(pluralWord);
                    }
                    else if (wordEmSizeDictionary.ContainsKey(word))
                    {
                        wordEmSizeDictionary[word]++;
                    }
                    else
                    {
                        wordEmSizeDictionary.Add(word, 1);
                    }
                }
            }

            // All words counted and in the dictionary.
            var wordSizeValues = wordEmSizeDictionary.Values;
            var maxWordEmSize = wordSizeValues.Max();
            var minWordSize = wordSizeValues.Min();
            if (FocusWord != null)
            {
                wordEmSizeDictionary[FocusWord] = maxWordEmSize = maxWordEmSize * FOCUS_WORD_SCALE;
            }
            float averageWordSize = wordSizeValues.Average();

            string[] sortedWordList = wordEmSizeDictionary.Keys
                .OrderByDescending(size => wordEmSizeDictionary[size])
                .Take(MaxRenderedWords == 0 ? ushort.MaxValue : MaxRenderedWords)
                .ToArray();

        }

        private async Task<string[]> ProcessLineAsync(string line)
        {
            return await Task.Run<string[]>(() =>
            {
                var words = new List<string>(line.Split(_splitChars, StringSplitOptions.RemoveEmptyEntries));
                words.RemoveAll(x => Array.IndexOf(_stopWords, x) == -1);
                return words.ToArray();
            });
        }
    }
}