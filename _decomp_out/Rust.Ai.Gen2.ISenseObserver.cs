namespace Rust.Ai.Gen2;

public interface ISenseObserver
{
	void OnPlayerSensed(BasePlayer player, float deltaTime);

	bool RefusesToTarget(BaseEntity entity);
}

