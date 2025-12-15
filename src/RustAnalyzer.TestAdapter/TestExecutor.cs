using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KS.RustAnalyzer.TestAdapter.Cargo;
using KS.RustAnalyzer.TestAdapter.Common;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Newtonsoft.Json;

namespace KS.RustAnalyzer.TestAdapter;

/// <summary>
/// Execution of tests happen by running an exe belonging to a test container.
///
/// By the time we are here, it is fine to assume that TestContainer is complete (i.e. filled with Exes).
/// </summary>
[ExtensionUri(Constants.ExecutorUriString)]
public class TestExecutor : BaseTestExecutor, ITestExecutor
{
    private bool _cancelled;

    /// <summary>
    /// Signature requried by ITestExecutor.
    /// </summary>
    public void RunTests(IEnumerable<TestCase> tests, IRunContext runContext, IFrameworkHandle frameworkHandle)
    {
        var ct = new CancellationToken(_cancelled);
        var tl = frameworkHandle.CreateTL();
        tl.L.WriteLine("RunTests starting. Executing {0} tests", tests.Count());
        var tasks = tests
            .GroupBy(t => t.Source)
            .Select(g => (g.Key, g.AsEnumerable()))
            .Select(async g => (await ((PathEx)g.Key).ReadTestContainerAsync(ct), g.Item2))
            .Select(async x =>
            {
                var (c, tcs) = await x;
                c.TestExes.ForEach(exe => RunAndRecordTestResultsFromOneExe(exe, tcs, TestRunParams.FromContainer(c), runContext.IsBeingDebugged, frameworkHandle, tl, ct));
            });

        Task.WaitAll(tasks.ToArray());
    }

    public override void RunTests(IEnumerable<PathEx> sources, IRunContext runContext, IFrameworkHandle frameworkHandle)
    {
        var ct = new CancellationToken(_cancelled);
        var tl = frameworkHandle.CreateTL();
        tl.L.WriteLine("RunTests starting. Executing {0} sources.", sources.Count());
        var tasks = sources.Select(async source => await RunTestsTestsFromOneSourceAsync(await source.ReadTestContainerAsync(ct), runContext, frameworkHandle, tl, ct));
        Task.WaitAll(tasks.ToArray());
    }

    /// <summary>
    /// Each TestContainer has multiple Exes, Each exe has multiple tests.
    /// Execution of tests happen by running the Exes. All in parallel.
    /// </summary>
    public static async Task RunTestsTestsFromOneSourceAsync(TestContainer container, IRunContext runContext, IFrameworkHandle fh, TL tl, CancellationToken ct)
    {
        foreach (var (tsi, tcs) in await container.DiscoverTestCasesFromOneSourceAsync(tl, ct))
        {
            RunAndRecordTestResultsFromOneExe(tsi.Exe, tcs, TestRunParams.FromContainer(container), runContext.IsBeingDebugged, fh, tl, ct);
        }
    }

    public void Cancel()
    {
        _cancelled = true;
    }

    private static void RunAndRecordTestResultsFromOneExe(PathEx exe, IEnumerable<TestCase> testCases, TestRunParams trp, bool isBeingDebugged, IFrameworkHandle fh, TL tl, CancellationToken ct)
    {
        tl.L.WriteLine("RunAndRecordTestResultsFromOneExe starting with {0}, {1}, {2}", trp.Source, exe, testCases.Count());
        if (!testCases.Any())
        {
            tl.L.WriteError("RunTestsFromOneSourceAsync: Something has gone wrong. Asking to run empty set of test cases. {0}, {1}", trp.Source, exe);
        }

        try
        {
            var envDict = trp.TestExecutionEnvironment.OverrideProcessEnvironment();

            // For WSL we must not pass the full Windows process environment; only pass explicit overrides.
            var envWsl = trp.TestExecutionEnvironment.ToNullSeparatedDictionary();
            var testCasesMap = testCases.ToImmutableDictionary(x => x.FullyQualifiedNameRustFormat());
            var args = testCases.Select(tc => tc.FullyQualifiedNameRustFormat());
            var grps = args
                .PartitionBasedOnMaxCombinedLength(20000)
                .Select(x =>
                    x
                        .Concat(new[] { "--exact", "--format", "json", "-Zunstable-options", "--report-time" })
                        .Concat(trp.AdditionalTestExecutionArguments.FromNullSeparatedArray()));
            Parallel.Invoke(
                grps
                    .Select(args => RunTestsFromOneExe(exe, args.ToArray(), testCasesMap, envDict, envWsl, tl, isBeingDebugged, fh, ct))
                    .Select(t => (Action)(() => t.Wait()))
                    .ToArray());
        }
        catch (Exception e)
        {
            tl.L.WriteError("RunTests failed with {0}", e);
            tl.T.TrackException(e);
            throw;
        }
    }

    private static async Task RunTestsFromOneExe(
        PathEx exe,
        string[] args,
        IReadOnlyDictionary<string, TestCase> testCasesMap,
        IDictionary<string, string> envDict,
        IDictionary<string, string> envWsl,
        TL tl,
        bool isBeingDebugged,
        IFrameworkHandle fh,
        CancellationToken ct)
    {
        // Check if this is a WSL test executable
        var isWsl = WslInfo.TryParse(exe, out var wslInfo);

        tl.L.WriteLine("... RunTestsFromOneExe starting with {0}, {1}, IsWsl: {2}", exe, args.Length, isWsl);
        tl.T.TrackEvent("RunTestsFromOneSourceAsync", ("IsBeingDebugged", $"{isBeingDebugged}"), ("Args", string.Join("|", args)), ("IsWsl", $"{isWsl}"));
        var trs = Enumerable.Empty<TestResult>();

        if (isBeingDebugged)
        {
            tl.L.WriteLine("RunTestsFromOneSourceAsync launching test under debugger.");
            if (isWsl)
            {
                // For WSL debugging, we need to convert paths and use appropriate mechanism
                // Note: LaunchProcessWithDebuggerAttached may not support WSL directly
                // This is a best-effort approach - full WSL test debugging may need additional work
                var linuxExe = wslInfo.ToLinuxPath(exe);
                var linuxWorkingDir = wslInfo.ToLinuxPath(exe.GetDirectoryName());
                tl.L.WriteLine("WSL test debug: exe={0}, workDir={1}", linuxExe, linuxWorkingDir);

                // Use wsl.exe to run the test, but note that debugger attach may not work correctly
                // Users can debug tests by running them individually with F5
                var wslExePath = WslInfo.GetWslExePath();
                var wslArgs = new List<string> { "-d", wslInfo.DistroName, "--cd", linuxWorkingDir, "--exec", linuxExe };
                wslArgs.AddRange(args);

                // Use System32 as the Windows working directory for wsl.exe (consistent with RunInWsl)
                var rc = fh.LaunchProcessWithDebuggerAttached(wslExePath, Environment.SystemDirectory, string.Join(" ", wslArgs), envWsl);
                if (rc != 0)
                {
                    tl.L.WriteError("RunTestsFromOneSourceAsync launching WSL test under debugger - returned {0}.", rc);
                }
            }
            else
            {
                var rc = fh.LaunchProcessWithDebuggerAttached(exe, exe.GetDirectoryName(), string.Join(" ", args), envDict);
                if (rc != 0)
                {
                    tl.L.WriteError("RunTestsFromOneSourceAsync launching test under debugger - returned {0}.", rc);
                }
            }
        }
        else
        {
            ProcessRunner testExeProc;

            if (isWsl)
            {
                // Run test executable via WSL
                var linuxExe = wslInfo.ToLinuxPath(exe);
                var linuxWorkingDir = wslInfo.ToLinuxPath(exe.GetDirectoryName());
                testExeProc = ToolchainServiceExtensions.RunInWsl(wslInfo, linuxExe, args, linuxWorkingDir, envWsl, ct);
            }
            else
            {
                testExeProc = ProcessRunner.Run(exe, args, exe.GetDirectoryName(), envDict, ct);
            }

            using (testExeProc)
            {
                tl.L.WriteLine("Started PID:{0} with args: {1}...", testExeProc.ProcessId, testExeProc.Arguments);
                var exitCode = await testExeProc;
                tl.L.WriteLine("... Finished PID {0} with exit code {1}.", testExeProc.ProcessId, testExeProc.ExitCode);

                trs = testExeProc.StandardOutputLines
                    .Skip(1)
                    .Take(testExeProc.StandardOutputLines.Count() - 2)
                    .Select(JsonConvert.DeserializeObject<TestRunInfo>)
                    .Where(x => x.Event != TestRunInfo.EventType.Started)
                    .OrderBy(x => x.FQN)
                    .Select(x => ToTestResult(exe, x, testCasesMap));

                var ec = testExeProc.ExitCode ?? 0;
                if (ec != 0 && !trs.Any())
                {
                    tl.L.WriteError("RunTestsFromOneSourceAsync test executable exited with code {0}.", ec);
                    throw new ApplicationException($"Test executable returned {ec}. Check above for the arguments passed to test executable by running it on the command line.");
                }

                tl.T.TrackEvent("RunTestsFromOneSourceAsync", ("Results", $"{trs.Count()}"));
            }
        }

        foreach (var tr in trs)
        {
            fh.RecordResult(tr);
        }
    }

    private static TestResult ToTestResult(PathEx exe, TestRunInfo tri, IReadOnlyDictionary<string, TestCase> testCasesMap)
    {
        return new TestResult(testCasesMap[tri.FQN])
        {
            DisplayName = tri.FQN.RustFQN2TestExplorerFQN(exe),
            ErrorMessage = string.Join("\n", new[] { tri.Message, tri.StdOut }.Where(x => !string.IsNullOrEmpty(x))),
            Outcome = GetOutcome(tri.Event),
            Duration = TimeSpan.FromSeconds(tri.ExecutionTime)
        };
    }

    private static TestOutcome GetOutcome(TestRunInfo.EventType @event)
    {
        switch (@event)
        {
            case TestRunInfo.EventType.Started:
                return TestOutcome.None;
            case TestRunInfo.EventType.Ok:
                return TestOutcome.Passed;
            case TestRunInfo.EventType.Failed:
                return TestOutcome.Failed;
            case TestRunInfo.EventType.Ignored:
                return TestOutcome.Skipped;
            default:
                return TestOutcome.None;
        }
    }

    public class TestRunParams
    {
        public PathEx Source { get; set; }

        public string AdditionalTestExecutionArguments { get; set; }

        public string TestExecutionEnvironment { get; set; }

        public static TestRunParams FromContainer(TestContainer tc)
        {
            return new TestRunParams
            {
                Source = tc.ThisPath,
                AdditionalTestExecutionArguments = tc.AdditionalTestExecutionArguments,
                TestExecutionEnvironment = tc.TestExecutionEnvironment,
            };
        }
    }
}
