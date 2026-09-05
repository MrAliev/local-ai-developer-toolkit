using System.Text.Json;

// A child process for the tests of SystemProcessRunner, and nothing else.
//
// Those tests used to launch PowerShell with a generated script. PowerShell is an interpreter
// with a startup cost that varies from half a second to more than ten on a loaded CI runner, so
// every budget in those tests was a bet on machine speed rather than an assertion about the
// runner — and one of them lost that bet: a run came back with no exit code and the failure read
// as a broken argument list. This starts in milliseconds and does exactly one thing per verb, so
// a budget there can go back to meaning "the child finished" instead of "the machine was quick".
//
// Keep the verbs boring. Anything with branching worth testing does not belong in a fixture.

if (args.Length == 0)
{
    Console.Error.Write("A verb is required.");
    return 2;
}

switch (args[0])
{
    case "echo-args":
        // Serialised rather than printed one per line: the point of the test using this is that
        // separators inside an argument survive, so the output must not be separator-delimited.
        Console.Out.Write(JsonSerializer.Serialize(args[1..]));
        return 0;

    case "sleep":
        Thread.Sleep(TimeSpan.FromSeconds(int.Parse(args[1])));
        return 0;

    case "write":
        // Standard output first and standard error second, both larger than any sane pipe
        // buffer: a parent that reads them in sequence rather than concurrently deadlocks here,
        // which is the behaviour the test is looking for.
        Console.Out.Write(new string('o', int.Parse(args[1])));
        Console.Error.Write(new string('e', int.Parse(args[2])));
        return 0;

    case "error-lines":
        // Numbered lines on standard error, one at a time, for a parent that claims to
        // hand them over as they arrive rather than at the end.
        for (var line = 1; line <= int.Parse(args[1]); line++)
        {
            Console.Error.WriteLine($"line {line}");
            Console.Error.Flush();
        }

        return 0;

    case "write-binary":
    {
        // Bytes that are not valid UTF-8 on purpose, written to the raw stream, so a parent that
        // decodes and re-encodes the output cannot reproduce them.
        var bytes = args[1..].Select(byte.Parse).ToArray();
        using var standardOutput = Console.OpenStandardOutput();
        standardOutput.Write(bytes, 0, bytes.Length);
        standardOutput.Flush();
        return 0;
    }

    case "write-pid-then-sleep":
        // The pid goes out before the sleep so a test can wait for the file instead of guessing
        // how long the child needs to get going.
        File.WriteAllText(args[1], Environment.ProcessId.ToString());
        Thread.Sleep(TimeSpan.FromSeconds(int.Parse(args[2])));
        return 0;

    default:
        Console.Error.Write($"Unknown verb '{args[0]}'.");
        return 2;
}
