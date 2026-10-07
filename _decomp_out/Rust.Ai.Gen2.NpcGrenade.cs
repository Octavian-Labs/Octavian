using System;
using UnityEngine;

namespace Rust.Ai.Gen2;

public class NpcGrenade : BaseEntity
{
	public GameObjectRef explosionEffect;

	public GameObjectRef fireballPrefab;

	public float speed = 10f;

	[NonSerialized]
	public NpcGrenadePositionHint grenadeHint;

	private double spawnTime;

	public override void ServerInit()
	{
		base.ServerInit();
		spawnTime = Time.realtimeSinceStartupAsDouble;
	}

	private void Update()
	{
		if (base.isServer && !base.IsDestroyed)
		{
			Vector3 vector = grenadeHint.transform.position + 1.8f * Vector3.up;
			Vector3 position = grenadeHint.landingPoint.position;
			Vector3 vector2 = ((vector + position) * 0.5f).WithY(Mathf.Max(vector.y, position.y) + grenadeHint.apexHeight);
			float num = (position - vector).MagnitudeXZ() / speed;
			float value = (float)(Time.realtimeSinceStartupAsDouble - spawnTime) / num;
			value = Mathf.Clamp01(value);
			base.transform.position = Vector3.Lerp(Vector3.Lerp(vector, vector2, value), Vector3.Lerp(vector2, position, value), value);
			if (value >= 1f)
			{
				FlameExplode();
				Kill();
			}
		}
	}

	public void FlameExplode(int numToCreate = 5)
	{
		Vector3 vector = base.transform.position + Vector3.up * 0.3f;
		Effect.server.Run(explosionEffect.resourcePath, vector, Vector3.up, null, broadcast: true);
		Collider component = GetComponent<Collider>();
		if ((bool)component)
		{
			component.enabled = false;
		}
		SpawnFireball(vector);
		for (int i = 0; i < numToCreate; i++)
		{
			Vector3 vector2 = Quaternion.Euler(0f, (float)i / (float)numToCreate * 360f, 0f) * Vector3.forward * 1.8f * UnityEngine.Random.Range(0.8f, 1.2f);
			Vector3 spawnPos = vector + vector2;
			if (GamePhysics.Trace(new Ray(vector, vector2), 0f, out var hitInfo, vector2.magnitude, 1237003025))
			{
				spawnPos = hitInfo.point - vector2.normalized * 0.5f;
			}
			SpawnFireball(spawnPos);
		}
	}

	private void SpawnFireball(Vector3 spawnPos)
	{
		BaseEntity baseEntity = GameManager.server.CreateEntity(fireballPrefab.resourcePath, spawnPos);
		if ((bool)baseEntity)
		{
			float y = UnityEngine.Random.Range(0f, 360f);
			Quaternion rotation = Quaternion.Euler(0f, y, 0f);
			baseEntity.transform.SetPositionAndRotation(spawnPos, rotation);
			baseEntity.creatorEntity = ((creatorEntity == null) ? baseEntity : creatorEntity);
			baseEntity.Spawn();
		}
	}

	public static bool SimulatePositionAtTime(Vector3 startPos, Vector3 endPos, float speed, float elapsedTime, out Vector3 pos, float gravity = -9.81f)
	{
		Vector3 vector = endPos - startPos;
		float magnitude = new Vector2(vector.x, vector.z).magnitude;
		float num;
		Vector3 vector2;
		if (magnitude < 0.001f)
		{
			num = 0.25f;
			vector2 = Vector3.zero;
		}
		else
		{
			vector2 = new Vector3(vector.x, 0f, vector.z).normalized;
			num = Mathf.Max(0.0001f, magnitude / speed);
		}
		float num2 = Mathf.Min(elapsedTime, num);
		pos = startPos;
		pos += vector2 * (speed * num2);
		float num3 = (endPos.y - startPos.y - 0.5f * gravity * num * num) / num;
		pos.y = startPos.y + num3 * num2 + 0.5f * gravity * num2 * num2;
		return elapsedTime >= num;
	}
}

