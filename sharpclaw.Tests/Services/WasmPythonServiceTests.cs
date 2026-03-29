using sharpclaw.Core;
using sharpclaw.Core.TaskManagement;
using sharpclaw.Services;
using Xunit;

namespace sharpclaw.Tests.Services;

public sealed class WasmPythonServiceTests
{
    [Fact]
    public async Task RunPython_UsesRunnerAndStreamsSuccessfulOutput()
    {
        var workspaceDirectory = CreateTemporaryDirectory();
        var sessionDirectory = CreateTemporaryDirectory();
        var executionDirectory = CreateTemporaryDirectory();
        var runner = new FakeRunner(new WasmPythonExecutionResult(
            Success: true,
            ExitCode: 0,
            Data: "hello-from-wasm",
            Error: "warning-from-wasm",
            NativeResultCode: 0,
            NativeResultMessage: "OK",
            TimedOut: false));

        try
        {
            var taskManager = new TaskManager();
            try
            {
                var agentContext = new AgentContext(taskManager);
                agentContext.SetWorkspaceDirPath(workspaceDirectory);
                agentContext.SetSessionDirPath(sessionDirectory);

                using var service = new WasmPythonService(agentContext, () => runner);

                var response = service.RunPython(
                    "print('hello-from-wasm')",
                    "Verify WasmPythonService can run Python code in /workspace",
                    timeOut: 12,
                    workingDirectory: executionDirectory);

                Assert.Contains("ok", response, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("taskId", response, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(1, taskManager.TaskCount);

                var managedTask = Assert.Single(taskManager.GetAllTasks());

                try
                {
                    await managedTask.WaitForCompletionAsync();

                    var output = managedTask.ReadChunk(
                        OutputStreamKind.Combined,
                        0,
                        managedTask.GetLength(OutputStreamKind.Combined));

                    Assert.Equal(workspaceDirectory, runner.InitializedWorkspaceDirectory);
                    Assert.Equal("print('hello-from-wasm')", runner.ExecutedCode);
                    Assert.Equal(executionDirectory, runner.ExecutionWorkingDirectory);
                    Assert.Equal(12000, runner.ExecutionTimeoutMs);
                    Assert.Equal(1, runner.InitCallCount);
                    Assert.Equal(1, runner.ExecuteCallCount);

                    Assert.False(managedTask.TimedOut);
                    Assert.Equal(0, managedTask.ExitCode);
                    Assert.Equal("exited", managedTask.GetStatus());
                    Assert.Contains("hello-from-wasm", output);
                    Assert.Contains("STDERR:\nwarning-from-wasm", output.Replace("\r\n", "\n", StringComparison.Ordinal));
                }
                finally
                {
                    taskManager.RemoveTask(managedTask.TaskId);
                }
            }
            finally
            {
                taskManager.Dispose();
            }
        }
        finally
        {
            TryDeleteDirectory(workspaceDirectory);
            TryDeleteDirectory(sessionDirectory);
            TryDeleteDirectory(executionDirectory);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sharpclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class FakeRunner : IRustPythonWasmRunner
    {
        private readonly WasmPythonExecutionResult _result;

        public FakeRunner(WasmPythonExecutionResult result)
        {
            _result = result;
        }

        public int InitCallCount { get; private set; }
        public int ExecuteCallCount { get; private set; }
        public string? InitializedWorkspaceDirectory { get; private set; }
        public string? ExecutedCode { get; private set; }
        public string? ExecutionWorkingDirectory { get; private set; }
        public int ExecutionTimeoutMs { get; private set; }

        public string WasmPath => "fake-rustpython.wasm";

        public void Init(string? workspaceRoot = null)
        {
            InitCallCount++;
            InitializedWorkspaceDirectory = workspaceRoot;
        }

        public WasmPythonExecutionResult ExecuteCode(string code, string workingDirectory, int timeoutMs = 180000)
        {
            ExecuteCallCount++;
            ExecutedCode = code;
            ExecutionWorkingDirectory = workingDirectory;
            ExecutionTimeoutMs = timeoutMs;
            return _result;
        }

        public void Dispose()
        {
        }
    }
}
