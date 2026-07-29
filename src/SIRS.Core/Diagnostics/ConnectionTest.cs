namespace Sirs.Core.Diagnostics;

public enum TestStepStatus
{
    Pending,
    Running,
    Passed,
    Failed,

    /// <summary>Not applicable to this server, e.g. the TLS step when the secure option is off.</summary>
    Skipped,
}

/// <summary>
/// One line of the test checklist. <see cref="Name"/> is what the user reads while it runs;
/// <see cref="Detail"/> is what they read afterwards.
/// </summary>
public sealed record TestStep(
    ConnectionTestStage Stage,
    string Name,
    TestStepStatus Status = TestStepStatus.Pending,
    string? Detail = null)
{
    public bool IsFinished => Status is TestStepStatus.Passed or TestStepStatus.Failed or TestStepStatus.Skipped;
}

public enum ConnectionTestStage
{
    ResolveAddress,
    OpenConnection,
    SecureConnection,
    IdentifyServer,
    SignIn,
    SendAudio,
}

public sealed record ConnectionTestResult(
    bool Success,
    IReadOnlyList<TestStep> Steps,
    string Summary,
    string? Advice,
    Servers.ServerType DetectedType)
{
    /// <summary>The step that stopped the test, if it failed.</summary>
    public TestStep? FailedStep => Steps.FirstOrDefault(s => s.Status == TestStepStatus.Failed);
}
