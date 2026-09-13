using Contracts;

namespace Orders.UnitTests;

public class CommandIdTests
{
    private static readonly Guid SagaId = Guid.Parse("0199a3c4-0000-7000-8000-000000000001");

    // Production failure: an orchestrator restarts mid-saga and re-derives a different
    // CommandId, so FakePay sees a new idempotency key and charges twice.
    [Fact]
    public void SameInputs_SameId_AcrossProcesses()
    {
        // Pinned value: if this changes, every in-flight saga loses its idempotency.
        CommandId.For(SagaId, StepNames.AuthorizePayment, Direction.Forward)
            .ShouldBe(CommandId.For(SagaId, StepNames.AuthorizePayment, Direction.Forward));
        CommandId.For(SagaId, StepNames.AuthorizePayment, Direction.Forward).ToString()
            .ShouldBe(Pinned.AuthorizeForward);
    }

    [Fact]
    public void DirectionAndStep_BothChangeTheId()
    {
        var forward = CommandId.For(SagaId, StepNames.AuthorizePayment, Direction.Forward);

        CommandId.For(SagaId, StepNames.AuthorizePayment, Direction.Compensate).ShouldNotBe(forward);
        CommandId.For(SagaId, StepNames.CapturePayment, Direction.Forward).ShouldNotBe(forward);
    }

    [Fact]
    public void IsAVersion8Guid()
    {
        CommandId.For(SagaId, StepNames.ReserveStock, Direction.Forward).Version.ShouldBe(8);
    }

    private static class Pinned
    {
        public const string AuthorizeForward = "8cdf3615-968b-8377-9b4f-74b1e63e47f0";
    }
}
