namespace PaddleOcrNet.Internal.Recognition;

/// <summary>
/// One character emitted by <see cref="CtcDecoder"/>, with the CTC timesteps it occupied. Word-level boxes
/// (<see cref="Models.RecognitionOptions.ReturnWordBoxes"/>) are derived from these positions.
/// </summary>
/// <param name="Token">The dictionary token that was emitted (usually one character).</param>
/// <param name="Probability">The winning class probability at <paramref name="FirstStep"/> — the value averaged into the line confidence.</param>
/// <param name="FirstStep">The timestep at which the character's run of identical argmax classes started (the step CTC best-path keeps).</param>
/// <param name="LastStep">The last timestep of that same run (equal to <paramref name="FirstStep"/> for a single-step spike).</param>
internal readonly record struct CtcCharacter(string Token, float Probability, int FirstStep, int LastStep);
