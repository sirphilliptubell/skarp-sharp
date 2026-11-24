namespace Fx.Unused;

// Fixture for the IDE0052 oracle comparison (unread-member) and transitive deadness.
public class UnreadCases
{
    private int _written;
    public void Write() { _written = 1; _count++; }
    private int _count;
    private readonly object _lock = new();
    private int Auto { get; } = Seed();
    private static int Seed() => 1;
    private System.Threading.Timer? _timer;
    public void Start() => _timer = new System.Threading.Timer(_ => { });
    private int _readByDead;
    public void Set() => _readByDead = 1;
    private int Dead() => _readByDead;
    private void Head() => Mid();
    private void Mid() { }
    internal int InternalWritten;
    public void SetInternal() => InternalWritten = 1;
    private int _read;
    public int Read() { _read = 2; return _read; }
    internal int ReadByTests;
    public void SetForTests() => ReadByTests = 1;
}
