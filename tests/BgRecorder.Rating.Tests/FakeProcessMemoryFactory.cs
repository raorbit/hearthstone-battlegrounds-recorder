namespace BgRecorder.Rating.Tests;

/// <summary>Hands the provider a pre-built heap, or a fixed attach fault, and counts attach attempts.</summary>
internal sealed class FakeProcessMemoryFactory : IProcessMemoryFactory
{
    private readonly IProcessMemory? _memory;
    private readonly AttachFault _fault;

    private FakeProcessMemoryFactory(IProcessMemory? memory, AttachFault fault)
    {
        _memory = memory;
        _fault = fault;
    }

    public int AttachCount { get; private set; }

    public static FakeProcessMemoryFactory Attaches(IProcessMemory memory) => new(memory, AttachFault.None);

    public static FakeProcessMemoryFactory Fails(AttachFault fault) => new(null, fault);

    public bool TryAttach(out IProcessMemory memory, out AttachFault fault)
    {
        AttachCount++;
        if (_memory is null)
        {
            memory = null!;
            fault = _fault;
            return false;
        }

        memory = _memory;
        fault = AttachFault.None;
        return true;
    }
}
