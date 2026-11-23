using System;
using System.Collections.Generic;
using System.Linq;

namespace OracleEdgeCases;

/// <summary>Lambdas, anonymous methods, local functions and LINQ.</summary>
public class NestedBodies
{
    private readonly Func<int, int> _doubler = x => x > 0 ? x * 2 : 0;

    public int LambdaWithBranch(List<int> items)
    {
        int count = 0;
        items.ForEach(x =>
        {
            if (x > 0)
            {
                count++;
            }
        });
        return count;
    }

    public int ExpressionLambda(List<int> items)
    {
        return items.Count(x => x > 0 && x < 10);
    }

    public int AnonymousMethod(List<int> items)
    {
        Predicate<int> isEven = delegate (int x) { return x % 2 == 0; };
        return items.FindAll(isEven).Count;
    }

    public int LocalFunction(int value)
    {
        return Twice(value) + 1;

        int Twice(int v)
        {
            if (v > 1000)
            {
                return v;
            }

            return v * 2;
        }
    }

    public int StaticLocalFunctionExpression(int value)
    {
        static int Square(int v) => v < 0 ? -v * v : v * v;
        return Square(value);
    }

    public int NestedLambdas(List<List<int>> groups)
    {
        return groups.Sum(g => g.Count(x => x > 0 || x == -1));
    }

    public IEnumerable<int> QuerySyntax(int[] values)
    {
        return from v in values
               where v > 0
               let w = v * 2
               orderby w
               select w;
    }

    public int UseField(int value) => _doubler(value);
}
