// Pattern: complex logic that runs only inside a lambda (the member itself only builds the query).
// Expected: no untested-complex-method finding: the lambda's lines belong to the member that declares it.
// Seen: Microsoft reports lambda lines under <>c classes.
namespace Mock.Lib;

public static class Tokenizer
{
    public static List<string> Kinds(IEnumerable<string> tokens) =>
        tokens.Select(t =>
        {
            if (t.Length == 0) return "empty";
            if (char.IsDigit(t[0])) return "number";
            if (char.IsLetter(t[0])) return "word";
            if (t == "(" || t == ")") return "paren";
            return "symbol";
        }).ToList();
}
