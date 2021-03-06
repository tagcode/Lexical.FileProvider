﻿// --------------------------------------------------------
// Copyright:      Toni Kalajainen
// Date:           2.1.2019
// Url:            http://lexical.fi
// --------------------------------------------------------
using Microsoft.Extensions.FileProviders;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Lexical.FileProvider.Utils
{
    /// <summary>
    /// This class scans directories and searches for files that match a wildcard and regex patterns.
    /// 
    /// The class itself is IEnumerable, it will start a new scan IEnumerator is acquired.
    /// 
    /// It uses concurrent threads for scanning. Tasks are spawned with Task.StartNew. 
    /// If TaskFactory is congested, the scanning may not start immediately. 
    /// Caller may provide customized TaskFactory to avoid issues.
    /// 
    /// The FileScanner is programmed so that it's internal separator is '/', and results use '/' as separator.
    /// For instance, to scan a network drive with RootFileProvider, use '/' separator after volume 
    /// <code>new FileScanner(root).AddWildcard(@"\\192.168.8.1\shared/*")</code>.
    /// </summary>
    public class FileScanner : IEnumerable<string>
    {
        /// <summary>
        /// Patterns by start path. 
        /// </summary>
        Dictionary<string, PatternSet> patterns = new Dictionary<string, PatternSet>();

        /// <summary>
        /// The factory that will be used for creating scanning threads.
        /// </summary>
        public TaskFactory TaskFactory = Task.Factory;

        /// <summary>
        /// A place to put errors. Caller must place value here before starting a scan.
        /// </summary>
        public IProducerConsumerCollection<Exception> errors;

        /// <summary>
        /// Root file provider
        /// </summary>
        public readonly IFileProvider FileProvider;

        /// <summary>
        /// Prefix to add to each file entries before they are matched.
        /// 
        /// For instance if "/" is used as prefix, then glob pattern "**/*.dll" can be used
        /// to match against all .dll files _including_ root, which would be "/" with prefix.
        /// </summary>
        public string RootPrefix { get; set; } = "";

        /// <summary>
        /// Function that tests whether to enter a directory. 
        /// </summary>
        public Func<string, bool> DirectoryEvaluator = DefaultDirectoryEvaluator;

        /// <summary>
        /// Default evaluator.
        /// </summary>
        static Func<string, bool> DefaultDirectoryEvaluator = path => true;

        /// <summary>
        /// Should file scanner return directories.
        /// </summary>
        public bool ReturnDirectories { get; set; } = false;

        /// <summary>
        /// Should file scanner return files.
        /// </summary>
        public bool ReturnFiles { get; set; } = true;

        /// <summary>
        /// Create new file scanner.
        /// </summary>
        /// <param name="fileProvider"></param>
        public FileScanner(IFileProvider fileProvider)
        {
            this.FileProvider = fileProvider ?? throw new ArgumentNullException(nameof(fileProvider));
        }

        /// <summary>
        /// Add a filename pattern, a pattern with path and wildcard, for example "*.dll", "folder/*.dll"
        /// </summary>
        /// <param name="pattern"></param>
        /// <returns>self</returns>
        public FileScanner AddWildcard(string pattern)
        {
            if (pattern == null) return this;

            int ix_questionmark = pattern.IndexOf('?'), ix_asterix = pattern.IndexOf('*');
            int ix_wildchar = ix_questionmark < 0 && ix_asterix < 0 ? -1 : ix_questionmark >= 0 ? (ix_asterix >= 0 ? Math.Min(ix_asterix, ix_questionmark) : ix_questionmark) : ix_asterix;

            string name, path;

            // Index of last separator before wildcard.
            int ix_separator = ix_wildchar >=0 ? pattern.LastIndexOf("/", ix_wildchar, StringComparison.InvariantCulture) : pattern.LastIndexOf("/", StringComparison.InvariantCulture);

            // There is no separator
            if (ix_separator < 0)
            {
                path = "";
                name = pattern;
            } else 
            // Starts with '/'
            if (ix_separator == 0)
            {
                path = "/";
                name = pattern.Substring(1);
            }
            // 'xxx/zzz'
            else
            {
                path = pattern.Substring(0, ix_separator);
                name = pattern.Substring(ix_separator + 1);
            }
            if (name == "") return this;

            // Add to pattern set
            PatternSet set;
            if (!patterns.TryGetValue(path, out set)) patterns[path] = set = new PatternSet();
            set.AddWildcard(PutTogetherPathAndName(path, name));

            return this;
        }

        internal static string PutTogetherPathAndName(string path, string name)
           => String.IsNullOrEmpty(path) ? name : (path.EndsWith("/", StringComparison.InvariantCulture) ? path + name : path + "/" + name);

        /// <summary>
        /// Adds glob pattern. 
        ///   "**" Matched to for any string of characters including directory separator.
        ///   "*" Matched for any string of characters within the same directory.
        ///   "?" Matched for one character excluding directory separator.
        /// </summary>
        /// <param name="pattern"></param>
        /// <returns></returns>
        public FileScanner AddGlobPattern(string pattern)
        {
            if (pattern == null) return this;

            int ix_questionmark = pattern.IndexOf('?'), ix_asterix = pattern.IndexOf('*');
            int ix_wildchar = ix_questionmark < 0 && ix_asterix < 0 ? -1 : Math.Min(ix_questionmark < 0 ? int.MaxValue : ix_questionmark, ix_asterix < 0 ? int.MaxValue : ix_asterix);

            string name, path;
            // Index of last separator before wildcard.
            int ix_separator = ix_wildchar >= 0 ? pattern.LastIndexOf("/", ix_wildchar, StringComparison.InvariantCulture) : pattern.LastIndexOf("/", StringComparison.InvariantCulture);

            // There is no separator
            if (ix_separator < 0)
            {
                path = "";
                name = pattern;
            }
            else
            // Starts with '/'
            if (ix_separator == 0)
            {
                path = "/";
                name = pattern.Substring(1);
            }
            // 'xxx/zzz'
            else
            {
                path = pattern.Substring(0, ix_separator);
                name = pattern.Substring(ix_separator + 1);
            }
            if (name == "") return this;

            // Add to pattern set
            PatternSet set;
            if (!patterns.TryGetValue(path, out set)) patterns[path] = set = new PatternSet();
            set.AddGlobPattern(PutTogetherPathAndName(path, name));

            return this;
        }

        /// <summary>
        /// Add regular expression pattern to the scanner.
        /// </summary>
        /// <param name="subpath">Sub-path to apply pattern from, for example "c:/temp/"</param>
        /// <param name="regex">Pattern. For example ".*\.zip"</param>
        /// <returns></returns>
        public FileScanner AddRegex(string subpath, Regex regex)
        {
            PatternSet set;
            if (!patterns.TryGetValue(subpath, out set)) patterns[subpath] = set = new PatternSet();
            set.AddRegex(regex);
            return this;
        }

        /// <summary>
        /// Set custom <paramref name="taskFactory"/> to use for constructing tasks.
        /// </summary>
        /// <param name="taskFactory"></param>
        /// <returns></returns>
        public FileScanner SetTaskFactory(TaskFactory taskFactory)
        {
            this.TaskFactory = taskFactory;
            return this;
        }

        /// <summary>
        /// Prefix to add to each file entries before they are matched.
        /// 
        /// For instance if "/" is used as prefix, then glob pattern "**/*.dll" can be used
        /// to match against all .dll files _including_ root, which would be "/" with prefix.
        /// </summary>
        public FileScanner SetPathPrefix(string pathPrefix)
        {
            this.RootPrefix = pathPrefix ?? "";
            return this;
        }

        /// <summary>
        /// Set collection where errors are written to.
        /// </summary>
        /// <param name="errors"></param>
        /// <returns></returns>
        public FileScanner SetErrorTarget(IProducerConsumerCollection<Exception> errors)
        {
            this.errors = errors;
            return this;
        }

        /// <summary>
        /// Set custom evaluator that chooses whether to enter a directory or not.
        /// </summary>
        /// <param name="func"></param>
        /// <returns></returns>
        public FileScanner SetDirectoryEvaluator(Func<string, bool> func)
        {
            this.DirectoryEvaluator = func;
            return this;
        }

        /// <summary>
        /// Should file scanner return directories.
        /// <paramref name="returnDirectories"/>
        /// </summary>
        public FileScanner SetReturnDirectories(bool returnDirectories)
        {
            this.ReturnDirectories = returnDirectories;
            return this;
        }

        /// <summary>
        /// Should file scanner return directories.
        /// <paramref name="returnFiles"/>
        /// </summary>
        public FileScanner SetReturnFiles(bool returnFiles)
        {
            this.ReturnFiles = returnFiles;
            return this;
        }

        /// <summary>
        /// Start multi-threaded scan operation.
        /// </summary>
        /// <returns>FileScannerEnumerator</returns>
        IEnumerator<string> IEnumerable<string>.GetEnumerator()
            => new PatternScanner(FileProvider, RootPrefix, patterns, TaskFactory, errors, DirectoryEvaluator, ReturnDirectories, ReturnFiles);

        /// <summary>
        /// Start multi-threaded scan operation.
        /// </summary>
        /// <returns>FileScannerEnumerator</returns>
        IEnumerator IEnumerable.GetEnumerator()
            => new PatternScanner(FileProvider, RootPrefix, patterns, TaskFactory, errors, DirectoryEvaluator, ReturnDirectories, ReturnFiles);
    }

    /// <summary>
    /// Resettable scan enumerator.
    /// </summary>
    public class PatternScanner : IEnumerator<string>
    {
        /// <summary>
        /// Collection where errors can be placed. Add collection here.
        /// </summary>
        public IProducerConsumerCollection<Exception> errors;
        IFileProvider fileProvider;
        List<KeyValuePair<string, PatternSet>> paths;
        ScanJob job;
        TaskFactory taskFactory;
        Func<string, bool> directoryEvaluator;
        string rootPrefix;
        bool returnDirectories;
        bool returnFiles;

        /// <summary>
        /// Create scanner that uses patterns.
        /// </summary>
        /// <param name="fileProvider"></param>
        /// <param name="rootPrefix"></param>
        /// <param name="patterns"></param>
        /// <param name="taskFactory"></param>
        /// <param name="errors"></param>
        /// <param name="directoryEvaluator"></param>
        /// <param name="returnDirectories"></param>
        /// <param name="returnFiles"></param>
        public PatternScanner(IFileProvider fileProvider, string rootPrefix, IEnumerable<KeyValuePair<string, PatternSet>> patterns, TaskFactory taskFactory, IProducerConsumerCollection<Exception> errors, Func<string, bool> directoryEvaluator, bool returnDirectories, bool returnFiles)
        {
            this.fileProvider = fileProvider;
            this.rootPrefix = rootPrefix;
            this.directoryEvaluator = directoryEvaluator;
            this.paths = new List<KeyValuePair<string, PatternSet>>(patterns);
            this.job = new ScanJob(fileProvider, rootPrefix, paths, errors, taskFactory, directoryEvaluator, returnDirectories, returnFiles);
            this.taskFactory = taskFactory;
            this.returnDirectories = returnDirectories;
            this.returnFiles = returnFiles;
            int count = Math.Max(1, paths.Count);
            for (int i=0; i<count; i++)
                taskFactory.StartNew(this.job.Scan);
        }

        /// <summary>
        /// Current path.
        /// </summary>
        public string Current => job?.Current;
        object IEnumerator.Current => job?.Current;

        /// <summary>
        /// Dispose scanner.
        /// </summary>
        public void Dispose()
        {
            var j = job;
            j?.Dispose();
            job = null;
        }

        /// <summary>
        /// Move to next path. May block thread if result is not ready.
        /// </summary>
        /// <returns></returns>
        public bool MoveNext()
        {
            var j = job;
            if (j == null) return false; else return j.MoveNext();
        }

        /// <summary>
        /// Start over.
        /// </summary>
        public void Reset()
        {
            var j = job;
            if (j == null) throw new ObjectDisposedException(nameof(PatternScanner));
            ScanJob newJob = new ScanJob(fileProvider, rootPrefix, paths, errors, taskFactory, directoryEvaluator, returnDirectories, returnFiles);
            j = Interlocked.Exchange(ref job, newJob);
            int count = Math.Max(1, paths.Count);
            for (int i = 0; i < count; i++)
                taskFactory.StartNew(this.job.Scan);
            j.Dispose();
        }
    }    

    /// <summary>
    /// A single scan job. 
    /// </summary>
    class ScanJob : IEnumerator<string>
    {
        IFileProvider fileProvider;
        string rootPrefix;
        IProducerConsumerCollection<Exception> errors;
        List<KeyValuePair<string, PatternSet>> paths;
        BlockingCollection<string> resultQueue = new BlockingCollection<string>();
        CancellationTokenSource cancelSource = new CancellationTokenSource();
        Func<string, bool> directoryEvaluator;
        bool returnDirectories;
        bool returnFiles;
        TaskFactory taskFactory;
        object monitor = new object();
        string current;
        int activeThreads, threads;

        public ScanJob(IFileProvider fileProvider, string rootPrefix, IEnumerable<KeyValuePair<string, PatternSet>> patterns, IProducerConsumerCollection<Exception> errors, TaskFactory taskFactory, Func<string, bool> directoryEvaluator, bool returnDirectories, bool returnFiles)
        {
            this.paths = new List<KeyValuePair<string, PatternSet>>(patterns);
            this.rootPrefix = rootPrefix;
            this.errors = errors;
            this.taskFactory = taskFactory;
            this.fileProvider = fileProvider;
            this.directoryEvaluator = directoryEvaluator;
            this.returnDirectories = returnDirectories;
            this.returnFiles = returnFiles;
        }

        public string Current => current;
        object IEnumerator.Current => current;

        public void Dispose()
        {
            cancelSource.Cancel();
            Monitor.Enter(monitor);
            Monitor.PulseAll(monitor);
            Monitor.Exit(monitor);
            cancelSource.Dispose();
        }

        void ProcessPath(KeyValuePair<string, PatternSet> path, List<string> threadLocalList)
        {
            // Directory entries
            threadLocalList.Clear();

            // Files
            try
            {
                foreach (var entry in fileProvider.GetDirectoryContents(path.Key))
                {
                    string subpath = FileScanner.PutTogetherPathAndName(path.Key, entry.Name);

                    if (entry.IsDirectory)
                    {
                        if (directoryEvaluator(subpath)) threadLocalList.Add(subpath);

                        // Add directory to result, if it matches filter
                        if (returnDirectories)
                        {
                            Match match = path.Value.MatcherFunc(subpath);
                            if (match != null && match.Success)
                                resultQueue.Add(String.IsNullOrEmpty(rootPrefix) ? subpath : rootPrefix + subpath);
                        }
                    } else
                    {
                        if (returnFiles)
                        {
                            // Add file to result, if it matches filter
                            Match match = path.Value.MatcherFunc(subpath);
                            if (match != null && match.Success)
                                resultQueue.Add(String.IsNullOrEmpty(rootPrefix) ? subpath : rootPrefix + subpath);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                errors?.TryAdd(e);
            }

            Monitor.Enter(monitor);
            try
            {
                // Add to paths
                paths.AddRange(threadLocalList.Select(dir => new KeyValuePair<string, PatternSet>(dir, path.Value)));
                threadLocalList.Clear();

                // Wakeup threads, after this monitor block
                if (activeThreads < threads) Monitor.PulseAll(monitor);

                // Start more threads
                int needThreads = Math.Min(paths.Count, Environment.ProcessorCount)-threads;
                while (needThreads-- > 0) taskFactory.StartNew(Scan);
            }
            finally
            {
                Monitor.Exit(monitor);
            }
        }

        /// <summary>
        /// Call this from every thread that participates in scanning job.
        /// </summary>
        public void Scan()
        {
            Monitor.Enter(monitor);
            threads++;
            Monitor.Exit(monitor);
            try
            {
                List<string> threadLocalList = new List<string>();
                bool isActive = false;

                while (!cancelSource.Token.IsCancellationRequested)
                {
                    KeyValuePair<string, PatternSet> path = new KeyValuePair<string, PatternSet>(null, null);

                    // Get next path
                    Monitor.Enter(monitor);
                    try
                    {
                        if (paths.Count > 0)
                        {
                            int ix = paths.Count - 1;
                            path = paths[ix];
                            paths.RemoveAt(ix);
                        }

                        // Add to active threads
                        if (path.Key != null && !isActive)
                        {
                            isActive = true;
                            activeThreads++;
                        }
                        else if (path.Key == null && isActive)
                        {
                            isActive = false;
                            activeThreads--;
                            // Nothing in the paths and no thread works on anything.
                            if (activeThreads == 0) {
                                cancelSource.Cancel();
                                Monitor.PulseAll(monitor);
                                break;
                            }
                        } else if (path.Key == null)
                        {
                            if (activeThreads == 0)
                            {
                                cancelSource.Cancel();
                                Monitor.PulseAll(monitor);
                            }
                            break;
                        }
                    }
                    finally
                    {
                        Monitor.Exit(monitor);
                    }

                    // Process path
                    if (path.Key != null) ProcessPath(path, threadLocalList);
                    // Wait until next path has been processed.
                    else
                    {
                        Monitor.Enter(monitor);
                        Monitor.Wait(monitor);
                        Monitor.Exit(monitor);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception e)
            {
                errors?.TryAdd(e);
            } finally
            {
                Monitor.Enter(monitor);
                threads--;
                Monitor.Exit(monitor);
            }
        }

        public bool MoveNext()
        {
            // Try take
            bool _canceled = cancelSource.IsCancellationRequested;
            if (resultQueue.TryTake(out current)) return true;
            if (_canceled) return false;
            try
            {
                current = resultQueue.Take(cancelSource.Token);
                return current != null;
            } catch (OperationCanceledException)
            {
                if (resultQueue.TryTake(out current)) return true;
                current = null;
                return false;
            }
        }

        public void Reset()
            => throw new NotImplementedException();
    }
}
