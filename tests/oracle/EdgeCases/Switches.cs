using System;

namespace OracleEdgeCases;

/// <summary>switch statements, switch expressions and patterns.</summary>
public class Switches
{
    public int SwitchWithDefault(int value)
    {
        switch (value)
        {
            case 1:
            case 2:
                return 10;
            case 3:
                return 20;
            default:
                return 0;
        }
    }

    public int SwitchWithoutDefault(int value)
    {
        switch (value)
        {
            case 1:
                return 1;
        }

        return 0;
    }

    public int SwitchSectionStatements(int value)
    {
        int result = 0;
        switch (value)
        {
            case 1:
                result = 5;
                result++;
                break;
            case 2:
            {
                result = 7;
                break;
            }
            default:
                result = -1;
                break;
        }

        return result;
    }

    public string SwitchPatternLabels(object item)
    {
        switch (item)
        {
            case int number when number > 100:
                return "big int";
            case int:
                return "int";
            case string { Length: 0 }:
                return "empty string";
            case null:
                return "null";
            default:
                return "other";
        }
    }

    public int SwitchGotoCase(int value)
    {
        int total = 0;
        switch (value)
        {
            case 1:
                total += 1;
                goto case 2;
            case 2:
                total += 2;
                goto default;
            default:
                total += 3;
                break;
        }

        return total;
    }

    public string SwitchExpression(int value) => value switch
    {
        1 => "one",
        2 => "two",
        _ => "many",
    };

    public string SwitchExpressionWithWhen(int value) => value switch
    {
        < 0 => "negative",
        var v when v % 2 == 0 => "even",
        var v when v > 100 && v < 1000 => "odd hundreds",
        _ => "odd",
    };

    public string SwitchExpressionNoDiscard(bool flag) => flag switch
    {
        true => "yes",
        false => "no",
    };

    public string PatternCombinators(int value) => value switch
    {
        1 or 2 => "low",
        > 10 and < 20 => "teen",
        not 0 => "other",
        _ => "zero",
    };

    public bool IsTypePattern(object item) => item is string;

    public bool IsDeclarationPattern(object item)
    {
        if (item is string text)
        {
            return text.Length > 0;
        }

        return false;
    }

    public bool IsOrPattern(char c) => c is 'a' or 'e' or 'i';

    public bool IsAndNotPattern(int value) => value is > 0 and not 5;

    public bool IsPropertyPattern(Uri uri) => uri is { IsAbsoluteUri: true, Port: 80 };

    public bool IsNullPattern(object? item) => item is not null;
}
