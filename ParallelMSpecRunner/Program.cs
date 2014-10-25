﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Machine.Specifications.Reporting.Integration;
using Machine.Specifications.Runner;
using Machine.Specifications.Runner.Impl;
using ParallelMSpecRunner.Reporting;
using ParallelMSpecRunner.Utils;

namespace ParallelMSpecRunner
{
    class Program
    {
        private readonly static CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly static object _outputLockObject = new object();
        private readonly static TeamCityReporter _teamCityGlobalReporter = new TeamCityReporter(Console.WriteLine, new TimingRunListener());

        static void Main(string[] args)
        {
            Environment.Exit((int)Run(args));
        }

        public static ExitCode Run(string[] arguments)
        {
            Options options = new Options();
            if (!options.ParseArguments(arguments)) {
                Console.WriteLine(Options.Usage());
                return ExitCode.Failure;
            }

            try {
               
                List<Assembly> assemblies = GetAssemblies(options);
                if (assemblies.Count == 0) {
                    Console.WriteLine(Options.Usage());
                    return ExitCode.Failure;
                }

                return RunAllInParallel(assemblies, options.GetRunOptions(), options.Threads).Result;
            } catch (Exception ex) {
                Console.WriteLine(ex.ToString());
                return ExitCode.Error;
            }
        }

        private static async Task<ExitCode> RunAllInParallel(List<Assembly> assemblies, RunOptions runOptions, int threads)
        {
            _teamCityGlobalReporter.OnRunStart();

            BlockingCollection<Assembly> workLoad = new BlockingCollection<Assembly>();

            foreach (Assembly assembly in assemblies)
                workLoad.Add(assembly);
            // This means that when the collection is emtpy and TryTake is called - it will throw
            workLoad.CompleteAdding();

            List<Task<ExitCode>> tasks = new List<Task<ExitCode>>();

            for (int i = 0; i < threads; i++) {

                tasks.Add(Task.Run(() => {
                    while (true) {
                        Assembly assembly = null;
                        bool takeSuccess = false;

                        try {
                            takeSuccess = workLoad.TryTake(out assembly, Timeout.Infinite, _cancellationTokenSource.Token);
                        } catch (OperationCanceledException) {
                            // cancellation token triggered
                            return ExitCode.Success;
                        }

                        // collection is empty
                        if (!takeSuccess)
                            return ExitCode.Success;

                        ExitCode status = RunAssembly(assembly, runOptions);
                        if (status != ExitCode.Success) {
                            // stop all processing across all workers
                            _cancellationTokenSource.Cancel();
                            return status;
                        }
                    }
                }));

            }

            ExitCode[] result = await Task.WhenAll(tasks);
            _teamCityGlobalReporter.OnRunEnd();
            if (result.Any(e => e == ExitCode.Error))
                return ExitCode.Error;
            else if (result.Any(e => e == ExitCode.Failure))
                return ExitCode.Failure;
            else
                return ExitCode.Success;
        }

        private static void WriteOutput(BufferedAssemblyTeamCityReporter reporter)
        {
            lock (_outputLockObject) {
                Console.Write(reporter.Buffer);
                Console.Out.Flush();
            }
        }

        private static ExitCode RunAssembly(Assembly assembly, RunOptions runOptions)
        {
            ISpecificationRunListener listener = new BufferedAssemblyTeamCityReporter(WriteOutput);

            ISpecificationRunner specificationRunner = new AppDomainRunner(listener, runOptions);

            specificationRunner.RunAssembly(assembly);

            if (listener is ISpecificationResultProvider) {
                var errorProvider = (ISpecificationResultProvider) listener;
                if (errorProvider.FailureOccurred) {
                    return ExitCode.Failure;
                }
            }

            return ExitCode.Success;
        }

        private static List<Assembly> GetAssemblies(Options options)
        {
            List<Assembly> assemblies = new List<Assembly>();

            List<string> assemblyFiles = new List<string>();
            if (options.AssemblyFiles != null)
                assemblyFiles.AddRange(options.AssemblyFiles);

            if (options.TestsDirectory != null && Directory.Exists(options.TestsDirectory)) {
                IEnumerable<string> files = Directory.EnumerateFiles(options.TestsDirectory, "*", SearchOption.AllDirectories);

                if (options.TestsFilePatterns != null && options.TestsFilePatterns.Count > 0) {
                    foreach (string filePattern in options.TestsFilePatterns)
                        assemblyFiles.AddRange(files.Where(file => Regex.IsMatch(file, filePattern)));
                } else { // look for *.dll
                    assemblyFiles.AddRange(files.Where(file => Regex.IsMatch(file, @".*\.dll$")));
                }
            }

            foreach (string assemblyName in assemblyFiles) {
                if (!File.Exists(assemblyName))
                    Console.WriteLine(String.Format("Error: Can't find assembly: {0}", assemblyName));

                var excludedAssemblies = new [] {
                     "Machine.Specifications.dll",
                     "Machine.Specifications.Clr4.dll"
                };
                if (excludedAssemblies.Any(x => Path.GetFileName(assemblyName) == x)) {
                    Console.WriteLine("Warning: Excluded {0} from the test run because the file name matches either of these: {1}", assemblyName, String.Join(", ", excludedAssemblies));
                    continue;
                }

                Assembly assembly = Assembly.LoadFrom(assemblyName);
                assemblies.Add(assembly);
            }

            return assemblies;
        }
    }
}
