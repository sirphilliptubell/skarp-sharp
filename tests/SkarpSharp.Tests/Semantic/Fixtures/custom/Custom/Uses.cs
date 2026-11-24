namespace Fx.Custom;

public class UsesExtra
{
    public int Read(Extra e) => e.Value;
}

#if REPO_BEFORE
public class FromBefore
{
    public int X { get; set; }
}
#endif
