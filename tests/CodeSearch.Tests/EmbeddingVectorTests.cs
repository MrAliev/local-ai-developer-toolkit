using CodeSearch.Core.Embedding;

namespace CodeSearch.Tests;

/// <summary>
/// Normalization refuses what it cannot normalize (#205): a zero vector used to pass
/// through silently and a non-finite magnitude produced NaNs that survived into the
/// durable index, destabilizing every ranking comparison they touched.
/// </summary>
public sealed class EmbeddingVectorTests
{
    [Fact]
    public void A_regular_vector_normalizes_to_unit_length()
    {
        var vector = new float[] { 3f, 4f };

        EmbeddingVector.Normalize(vector);

        Assert.Equal(0.6f, vector[0], 3);
        Assert.Equal(0.8f, vector[1], 3);
    }

    [Fact]
    public void A_zero_vector_is_refused_rather_than_passed_through()
    {
        var error = Assert.Throws<InvalidDataException>(
            () => EmbeddingVector.Normalize(new float[4]));

        Assert.Contains("zero vector", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_finite_magnitude_is_refused()
    {
        var error = Assert.Throws<InvalidDataException>(
            () => EmbeddingVector.Normalize([float.NaN, 1f]));

        Assert.Contains("not finite", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_infinite_component_is_refused()
    {
        // The shape M15 describes: a finite double past float.MaxValue casts to Infinity.
        // The cast site refuses it first; this is the second line of the same defence.
        var error = Assert.Throws<InvalidDataException>(
            () => EmbeddingVector.Normalize([float.PositiveInfinity, 1f]));

        Assert.Contains("not finite", error.Message, StringComparison.Ordinal);
    }
}
