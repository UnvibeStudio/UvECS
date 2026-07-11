using System.Diagnostics;

namespace UvEcs.Bench;

public static class Harness
{
    private const int Rounds = 25;

    /// <summary>
    /// Общий прогрев всех вариантов, чередование порядка, медиана, печать разброса.
    /// Разница меньше собственного разброса варианта — не разница (§13 спеки).
    /// </summary>
    public static void Compare(string title, int iterations, params (string Name, Action Body)[] variants)
    {
        Console.WriteLine($"\n=== {title} ===");

        for (int i = 0; i < 20_000; i++)
            foreach (var v in variants) v.Body();

        var samples = new double[variants.Length][];
        for (int i = 0; i < variants.Length; i++) samples[i] = new double[Rounds];

        for (int round = 0; round < Rounds; round++)
        {
            // чередуем порядок: первый замер систематически хуже
            bool forward = (round & 1) == 0;
            for (int k = 0; k < variants.Length; k++)
            {
                int i = forward ? k : variants.Length - 1 - k;
                samples[i][round] = Measure(variants[i].Body, iterations);
            }
        }

        double? baseline = null;
        double baselineSpread = 0;

        for (int i = 0; i < variants.Length; i++)
        {
            var s = samples[i];
            Array.Sort(s);
            double min = s[0], med = s[Rounds / 2], max = s[^1];
            double spread = max / min - 1;

            Console.WriteLine($"  {variants[i].Name,-28} min {min,7:F2}  med {med,7:F2}  max {max,7:F2} мкс  (разброс {spread * 100,4:F0}%)");

            if (i == 0) { baseline = med; baselineSpread = spread; }
            else
            {
                double ratio = med / baseline!.Value;
                bool significant = Math.Abs(ratio - 1) > baselineSpread;
                Console.WriteLine($"  {"",-28} -> {ratio:F3}x относительно «{variants[0].Name}» " +
                                  (significant ? "(значимо)" : "(В ПРЕДЕЛАХ ШУМА — разницы нет)"));
            }
        }
    }

    private static double Measure(Action body, int iterations)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) body();
        sw.Stop();
        return sw.Elapsed.TotalMicroseconds / iterations;
    }
}
