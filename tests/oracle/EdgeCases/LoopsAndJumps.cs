using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OracleEdgeCases;

/// <summary>Loops, jumps, exceptions and resource statements.</summary>
public class LoopsAndJumps
{
    public int ForLoop(int count)
    {
        int sum = 0;
        for (int i = 0; i < count; i++)
        {
            sum += i;
        }

        return sum;
    }

    public int ForLoopUnbraced(int count)
    {
        int sum = 0;
        for (int i = 0; i < count; i++) sum += i;
        return sum;
    }

    public int ForEachLoop(int[] values)
    {
        int sum = 0;
        foreach (var v in values)
        {
            sum += v;
        }

        return sum;
    }

    public int ForEachDeconstruction(Dictionary<string, int> map)
    {
        int sum = 0;
        foreach (var (_, v) in map)
        {
            sum += v;
        }

        return sum;
    }

    public async Task<int> AwaitForEach(IAsyncEnumerable<int> values)
    {
        int sum = 0;
        await foreach (var v in values)
        {
            sum += v;
        }

        return sum;
    }

    public int WhileLoop(int value)
    {
        while (value > 10)
        {
            value /= 2;
        }

        return value;
    }

    public int DoWhileLoop(int value)
    {
        do
        {
            value--;
        }
        while (value > 0);
        return value;
    }

    public int BreakAndContinue(int[] values)
    {
        int sum = 0;
        foreach (var v in values)
        {
            if (v < 0)
            {
                continue;
            }

            if (v > 100)
            {
                break;
            }

            sum += v;
        }

        return sum;
    }

    public int GotoLabel(int value)
    {
    again:
        value++;
        if (value < 5)
        {
            goto again;
        }

        return value;
    }

    public void EmptyStatementAndBareReturn(bool flag)
    {
        ;
        if (flag)
        {
            return;
        }

        Console.WriteLine();
    }

    public int UninitializedLocal()
    {
        int x;
        x = 3;
        int a = 1, b = 2;
        return x + a + b;
    }

    public void TryCatch()
    {
        try
        {
            Console.WriteLine("work");
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine("invalid");
        }
        catch (Exception)
        {
            throw;
        }
    }

    public void TryCatchWhen(int code)
    {
        try
        {
            Console.WriteLine("work");
        }
        catch (Exception e) when (e.HResult == code)
        {
            Console.WriteLine("filtered");
        }
        catch (Exception e) when (e.HResult > 0 && code > 0)
        {
            Console.WriteLine("filtered with and");
        }
    }

    public void TryFinally()
    {
        try
        {
            Console.WriteLine("work");
        }
        finally
        {
            Console.WriteLine("done");
        }
    }

    public void ThrowStatement(string? value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }
    }

    public string ThrowExpression(string? value) => value ?? throw new ArgumentNullException(nameof(value));

    public void UsingStatement(IDisposable resource)
    {
        using (resource)
        {
            Console.WriteLine("using");
        }
    }

    public void UsingDeclaration(Func<IDisposable> factory)
    {
        using var resource = factory();
        Console.WriteLine("using declaration");
    }

    public void LockStatement(object gate)
    {
        lock (gate)
        {
            Console.WriteLine("locked");
        }
    }

    public int CheckedBlock(int a, int b)
    {
        checked
        {
            return a * b;
        }
    }

    public IEnumerable<int> YieldReturn(int count)
    {
        for (int i = 0; i < count; i++)
        {
            yield return i;
        }

        yield break;
    }

    public void NestedBlock()
    {
        {
            Console.WriteLine("inner");
        }
    }
}
