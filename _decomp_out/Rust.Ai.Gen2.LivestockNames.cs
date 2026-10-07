using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Facepunch;
using UnityEngine;

namespace Rust.Ai.Gen2;

public static class LivestockNames
{
	private class Pack
	{
		public string[] MaleOnly;

		public string[] FemaleOnly;

		public string[] Unisex;

		public string[] MaleDraw;

		public string[] FemaleDraw;

		public TextInfo Casing;
	}

	[Serializable]
	private class DataFile
	{
		public string[] Male;

		public string[] Female;

		public string[] Unisex;
	}

	public const int ClassifiedCount = 1000;

	public const string DefaultLocale = "en";

	public const string DevLocale = "devs";

	public static readonly string[] ShippedLocales = new string[8] { "en", "ru", "tr", "pt-BR", "zh-CN", "es-ES", "de", "fr" };

	private const string FileNamePrefix = "LivestockNames";

	private const string FileNameSuffix = ".json";

	private static readonly Dictionary<string, Pack> packs = new Dictionary<string, Pack>();

	public static string[] MaleOnly => MaleOnlyIn("en");

	public static string[] FemaleOnly => FemaleOnlyIn("en");

	public static string[] Unisex => UnisexIn("en");

	public static string[] MaleOnlyIn(string locale)
	{
		return PackFor(locale).MaleOnly;
	}

	public static string[] FemaleOnlyIn(string locale)
	{
		return PackFor(locale).FemaleOnly;
	}

	public static string[] UnisexIn(string locale)
	{
		return PackFor(locale).Unisex;
	}

	public static string[] PoolFor(bool male)
	{
		return PoolFor(male, "en");
	}

	public static string[] PoolFor(bool male, string locale)
	{
		Pack pack = PackFor(locale);
		if (!male)
		{
			return pack.FemaleDraw;
		}
		return pack.MaleDraw;
	}

	public static bool HasPack(string locale)
	{
		return CanName(PackFor(locale));
	}

	private static bool CanName(Pack pack)
	{
		if (pack.MaleDraw.Length != 0)
		{
			return pack.FemaleDraw.Length != 0;
		}
		return false;
	}

	public static string Capitalise(string name, string locale)
	{
		if (string.IsNullOrEmpty(name))
		{
			return name;
		}
		return PackFor(locale).Casing.ToUpper(name[0]) + name.Substring(1);
	}

	private static Pack PackFor(string locale)
	{
		if (string.IsNullOrEmpty(locale))
		{
			locale = "en";
		}
		if (packs.TryGetValue(locale, out var value))
		{
			return value;
		}
		value = ReadPack(locale);
		packs[locale] = value;
		return value;
	}

	private static Pack ReadPack(string locale)
	{
		string text = ((locale == "en") ? "LivestockNames.json" : ("LivestockNames." + locale + ".json"));
		try
		{
			DataFile dataFile = JsonUtility.FromJson<DataFile>(File.ReadAllText(Path.Combine(UnityEngine.Application.streamingAssetsPath, text)));
			Pack pack = Build(locale, dataFile.Male, dataFile.Female, dataFile.Unisex);
			if (locale == "en" && !CanName(pack))
			{
				Debug.LogError("Livestock names in " + text + " classified nothing, so it is not the shape this expects. Falling back to the unsorted pool.");
				return FlatPool();
			}
			return pack;
		}
		catch (Exception ex)
		{
			if (locale != "en")
			{
				Debug.LogWarning("No livestock name pack for " + locale + " (" + text + "), so no animal is named in it. " + ex.Message);
				return Build(locale, null, null, null);
			}
			Debug.LogError("Livestock names could not be read from " + text + ", falling back to the unsorted pool. " + ex.Message);
			return FlatPool();
		}
	}

	private static Pack FlatPool()
	{
		try
		{
			string[] array = new string[1000];
			for (int i = 0; i < array.Length; i++)
			{
				array[i] = RandomUsernames.Get(i);
			}
			return Build("en", array, array, null);
		}
		catch (Exception ex)
		{
			Debug.LogError("Livestock names could not fall back to RandomUsernames either. " + ex.Message);
			return Build("en", null, null, null);
		}
	}

	private static Pack Build(string locale, string[] male, string[] female, string[] unisex)
	{
		Pack pack = new Pack();
		pack.MaleOnly = male ?? Array.Empty<string>();
		pack.FemaleOnly = female ?? Array.Empty<string>();
		pack.Unisex = unisex ?? Array.Empty<string>();
		pack.MaleDraw = Concat(pack.MaleOnly, pack.Unisex);
		pack.FemaleDraw = Concat(pack.FemaleOnly, pack.Unisex);
		pack.Casing = CasingFor(locale);
		return pack;
	}

	private static TextInfo CasingFor(string locale)
	{
		try
		{
			return CultureInfo.GetCultureInfo(locale).TextInfo;
		}
		catch (CultureNotFoundException)
		{
			return CultureInfo.InvariantCulture.TextInfo;
		}
	}

	private static string[] Concat(string[] first, string[] second)
	{
		string[] array = new string[first.Length + second.Length];
		Array.Copy(first, 0, array, 0, first.Length);
		Array.Copy(second, 0, array, first.Length, second.Length);
		return array;
	}
}

