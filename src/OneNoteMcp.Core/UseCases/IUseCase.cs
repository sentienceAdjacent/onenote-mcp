namespace OneNoteMcp.Core.UseCases;

/// <summary>One use case backs each tool and declares its one <see cref="OperationClass"/> (AD-5, AD-7).</summary>
public interface IUseCase
{
    OperationClass Class { get; }
}
