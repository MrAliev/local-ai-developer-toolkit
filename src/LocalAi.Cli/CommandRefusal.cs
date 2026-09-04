namespace LocalAi.Cli;

/// <summary>
/// A refusal in both faces at once: the token a program branches on, and the sentence a person
/// reads.
///
/// They are separate fields because they answer to different rules. <see cref="Message"/> follows
/// the reader and is reworded whenever it turns out to be unclear — this repository does that
/// often, and the <c>&lt;comment&gt;</c> on almost any catalogue key says why. <see cref="Code"/>
/// is wire format: never translated, never reworded without a schema bump, and never reconstructed
/// by parsing the message.
///
/// The tokens are <c>subject_state</c> — a noun naming the thing that was wrong, then what was
/// wrong with it — and they never name the command, because the envelope already carries that and
/// the same refusal recurs across commands. An option's subject is its own spelling with the
/// leading dashes dropped: <c>--root</c> gives <c>root_…</c>, <c>--max-inline-files</c> gives
/// <c>max_inline_files_…</c>. The launcher and the broker already speak this way
/// (<c>current_pointer_missing</c>, <c>broker_start_timeout</c>).
/// </summary>
public sealed record CommandRefusal(string Code, string Message);
