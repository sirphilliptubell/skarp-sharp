using System;
using System.Collections.Generic;

namespace OracleEdgeCases;

/// <summary>if, else-if chains, conditional and logical operators.</summary>
public class Branching
{
    public int EmptyMethod()
    {
        return 0;
    }

    public void VoidEmpty()
    {
    }

    public int SingleIf(int value)
    {
        if (value > 0)
        {
            return 1;
        }

        return 0;
    }

    public int IfElse(int value)
    {
        if (value > 0)
        {
            return 1;
        }
        else
        {
            return -1;
        }
    }

    public string ElseIfChain(int value)
    {
        if (value < 0)
        {
            return "negative";
        }
        else if (value == 0)
        {
            return "zero";
        }
        else if (value < 10)
        {
            return "small";
        }
        else
        {
            return "large";
        }
    }

    public int UnbracedIfBodies(int value)
    {
        int result = 0;
        if (value > 0) result = 1;
        if (value > 10) result = 2; else result = 3;
        return result;
    }

    public bool LogicalAnd(bool a, bool b) => a && b;

    public bool LogicalOr(bool a, bool b) => a || b;

    public bool LogicalMixed(bool a, bool b, bool c, bool d)
    {
        var x = a && (b || c) && !d;
        return x;
    }

    public bool NonShortCircuitBool(bool a, bool b, bool c)
    {
        var x = a & b | c;
        return x ^ a;
    }

    public bool NonShortCircuitEvidentBool(int a, int b)
    {
        return a > 0 & b > 0 | a == b;
    }

    public bool NonShortCircuitNotOperand(bool a, bool b) => !a & !b;

    public bool NonShortCircuitMixedOperand(bool a, int b) => a | b > 0;

    public bool CompoundBoolAssignment(bool a, bool b)
    {
        a &= b;
        a |= !b;
        return a;
    }

    [Obsolete("attribute on a member")]
    public int AttributedMethod(int value) => value;

    public int NonShortCircuitInt(int a, int b)
    {
        return (a & b) | (a ^ b);
    }

    public int Ternary(int value) => value > 0 ? value : -value;

    public int NestedTernary(int value)
    {
        var sign = value > 0 ? 1 : value < 0 ? -1 : 0;
        return sign;
    }

    public string Coalesce(string? first, string? second) => first ?? second ?? "none";

    public List<int> CoalesceAssignment(List<int>? items)
    {
        items ??= new List<int>();
        return items;
    }

    public int? ConditionalAccess(string? text) => text?.Length;

    public int? ConditionalAccessChain(Uri? uri) => uri?.Host?.Length;

    public char? ConditionalElementAccess(string[]? names) => names?[0]?[0];

    public int ConditionalAccessWithCoalesce(string? text) => text?.Length ?? 0;
}
