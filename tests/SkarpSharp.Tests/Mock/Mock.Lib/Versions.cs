// Pattern: one member per .NET version in an exact-symbol chain, more versions than the variant cap.
// Expected: the file is capped (variants.cap 32 of 40 planned): the highest and lowest frameworks first, then upward;
// the branches cut by the cap are counted as not planned; no finding.
// Seen: in real multi-targeted libraries, full combinations hit the cap in a share of the files that use #if.
namespace Mock.Lib;

public static class Versions
{
#if NET5_0
    public static int Major() => 5;
#elif NET6_0
    public static int Major() => 6;
#elif NET7_0
    public static int Major() => 7;
#elif NET8_0
    public static int Major() => 8;
#elif NET9_0
    public static int Major() => 9;
#elif NET10_0
    public static int Major() => 10;
#elif NET11_0
    public static int Major() => 11;
#elif NET12_0
    public static int Major() => 12;
#elif NET13_0
    public static int Major() => 13;
#elif NET14_0
    public static int Major() => 14;
#elif NET15_0
    public static int Major() => 15;
#elif NET16_0
    public static int Major() => 16;
#elif NET17_0
    public static int Major() => 17;
#elif NET18_0
    public static int Major() => 18;
#elif NET19_0
    public static int Major() => 19;
#elif NET20_0
    public static int Major() => 20;
#elif NET21_0
    public static int Major() => 21;
#elif NET22_0
    public static int Major() => 22;
#elif NET23_0
    public static int Major() => 23;
#elif NET24_0
    public static int Major() => 24;
#elif NET25_0
    public static int Major() => 25;
#elif NET26_0
    public static int Major() => 26;
#elif NET27_0
    public static int Major() => 27;
#elif NET28_0
    public static int Major() => 28;
#elif NET29_0
    public static int Major() => 29;
#elif NET30_0
    public static int Major() => 30;
#elif NET31_0
    public static int Major() => 31;
#elif NET32_0
    public static int Major() => 32;
#elif NET33_0
    public static int Major() => 33;
#elif NET34_0
    public static int Major() => 34;
#elif NET35_0
    public static int Major() => 35;
#elif NET36_0
    public static int Major() => 36;
#elif NET37_0
    public static int Major() => 37;
#elif NET38_0
    public static int Major() => 38;
#elif NET39_0
    public static int Major() => 39;
#elif NET40_0
    public static int Major() => 40;
#elif NET41_0
    public static int Major() => 41;
#elif NET42_0
    public static int Major() => 42;
#elif NET43_0
    public static int Major() => 43;
#elif NET44_0
    public static int Major() => 44;
#endif
}
