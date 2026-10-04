using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeskCards.Testing;

internal static partial class Program
{
    private static void TestSessionTests()
    {
        Test("test session: desktop is restored after success and failure", () =>
        {
            foreach (bool fail in new[] { false, true })
            {
                var calls = new List<string>();
                var expected = new IOException("test failure");
                Exception? caught = null;
                try
                {
                    TestSession.RunWithDesktop(() => calls.Add("minimize"), () =>
                    {
                        calls.Add("run");
                        if (fail) throw expected;
                    }, () => calls.Add("restore"));
                }
                catch (Exception ex) { caught = ex; }
                Check(calls.SequenceEqual(new[] { "minimize", "run", "restore" }));
                Check(fail ? ReferenceEquals(caught, expected) : caught == null);
            }
        });
        Test("test session: failed minimize never restores unrelated desktop state", () =>
        {
            bool ran = false, restored = false;
            try { TestSession.RunWithDesktop(() => throw new IOException("shell unavailable"), () => ran = true, () => restored = true); }
            catch (IOException) { }
            Check(!ran && !restored);
        });
    }
}
