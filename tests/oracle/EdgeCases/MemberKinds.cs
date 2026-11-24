using System;
using System.Collections.Generic;

namespace OracleEdgeCases;

/// <summary>Properties, indexers, constructors, operators, events and finalizers.</summary>
public class MemberKinds
{
    private static readonly Dictionary<string, int> s_defaults;
    private readonly int[] _values = new int[4];
    private int _count = 1;
    private string _name = "x";
    private EventHandler? _changed;

    static MemberKinds()
    {
        s_defaults = new Dictionary<string, int>();
        s_defaults["a"] = 1;
    }

    public MemberKinds()
    {
        _count = 2;
    }

    public MemberKinds(int count) : this()
    {
        _count = count > 0 ? count : 0;
    }

    public MemberKinds(string name) => _name = name ?? "default";

    public MemberKinds(int? count, bool flag) : this(count ?? (flag ? 1 : 0))
    {
    }

    public MemberKinds(List<int> items) : this(items.FindAll(x => x > 0 && x < 100).Count)
    {
        _name = items.Count > 0 ? "items" : "none";
    }

    ~MemberKinds()
    {
        _changed = null;
    }

    public int AutoProperty { get; set; }

    public int AutoPropertyWithInitializer { get; set; } = 5;

    public string InitOnly { get; init; } = "";

    public int ExpressionProperty => _count * 2;

    public int ExpressionAccessors
    {
        get => _count;
        set => _count = value < 0 ? 0 : value;
    }

    public string BlockAccessors
    {
        get
        {
            if (_name.Length == 0)
            {
                return "empty";
            }

            return _name;
        }
        set
        {
            _name = value ?? throw new ArgumentNullException(nameof(value));
        }
    }

    public string InitAccessor
    {
        get { return _name; }
        init { _name = value.Length > 0 ? value : "init"; }
    }

    public int this[int index]
    {
        get => index >= 0 && index < _values.Length ? _values[index] : -1;
        set
        {
            if (index >= 0)
            {
                _values[index] = value;
            }
        }
    }

    public string this[string key] => key.Length > 0 ? key : "none";

    public event EventHandler? Changed
    {
        add
        {
            if (value != null)
            {
                _changed += value;
            }
        }
        remove
        {
            _changed -= value;
        }
    }

    public event EventHandler? FieldLikeEvent;

    public static MemberKinds operator +(MemberKinds left, MemberKinds right)
    {
        return new MemberKinds(left._count + right._count);
    }

    public static bool operator ==(MemberKinds? left, MemberKinds? right) =>
        left is null ? right is null : right is not null && left._count == right._count;

    public static bool operator !=(MemberKinds? left, MemberKinds? right) => !(left == right);

    public static implicit operator int(MemberKinds value) => value?._count ?? 0;

    public static explicit operator MemberKinds(int value)
    {
        return new MemberKinds(value);
    }

    public override bool Equals(object? obj) => obj is MemberKinds other && other._count == _count;

    public override int GetHashCode() => _count;

    public void RaiseFieldLike() => FieldLikeEvent?.Invoke(this, EventArgs.Empty);

    public int OptionalParameter(int value = 3, string text = "a")
    {
        return value + text.Length;
    }
}

public abstract class AbstractMembers
{
    public abstract int AbstractMethod(int value);

    public virtual int VirtualMethod(int value) => value;

    public abstract int AbstractProperty { get; }
}

public interface IEdgeContract
{
    int InterfaceMethod(int value);

    int DefaultInterfaceMethod(int value) => value > 0 ? value : 0;
}

public record PositionalRecord(int X, string Name)
{
    public bool IsOrigin => X == 0 && Name.Length == 0;

    public int Doubled() => X * 2;
}

public record class RecordWithBody
{
    public int Value { get; init; }

    public RecordWithBody(int value)
    {
        Value = value < 0 ? 0 : value;
    }
}

public readonly struct ValueHolder
{
    private readonly int _value;

    public ValueHolder(int value)
    {
        _value = value;
    }

    public int Value => _value;
}

public class PrimaryConstructorClass(int seed)
{
    private readonly int _seed = seed > 0 ? seed : 1;

    public int Next() => _seed + 1;
}

public partial class PartialType
{
    public int FirstPart(int value) => value + 1;

    partial void Hook(int value);

    public void CallHook(int value) => Hook(value);
}

public partial class PartialType
{
    public int SecondPart(int value)
    {
        return value > 0 ? value : 0;
    }

    partial void Hook(int value)
    {
        if (value > 0)
        {
            Console.WriteLine(value);
        }
    }
}

public class GenericHolder<T> where T : class
{
    public T? Value { get; set; }

    public TOut Map<TOut>(Func<T, TOut> mapper, TOut fallback) => Value is null ? fallback : mapper(Value);

    public class Inner
    {
        public int InnerMethod(int value) => value * 3;
    }
}

public static class Extensions
{
    public static bool IsBlank(this string? text) => text == null || text.Trim().Length == 0;

    public static int Overloaded(int value) => value;

    public static int Overloaded(long value) => (int)value;

    public static int Overloaded(ref int value, out int other)
    {
        other = value;
        return value;
    }

    public static int ParamsArray(params int[] values) => values.Length;

    public static int TupleParameter((int A, int B) pair) => pair.A + pair.B;

    public static int NullableParameter(int? value, List<string?> names) => value ?? names.Count;
}
