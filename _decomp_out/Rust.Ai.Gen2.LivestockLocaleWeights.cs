using ConVar;
using UnityEngine;

namespace Rust.Ai.Gen2;

public static class LivestockLocaleWeights
{
	private static readonly int[] tally = new int[LivestockNames.ShippedLocales.Length];

	private static int talliedFrame = -1;

	private static string talliedHouse;

	private static string warnedLocale;

	public static string Roll()
	{
		if (!Livestock.localeNames)
		{
			return "en";
		}
		string text = HouseLocale();
		if (talliedFrame != UnityEngine.Time.frameCount || talliedHouse != text)
		{
			Count(tally, text);
			talliedFrame = UnityEngine.Time.frameCount;
			talliedHouse = text;
		}
		return PickFrom(tally, text);
	}

	public static string PickFrom(int[] weights, string house)
	{
		int num = 0;
		int num2 = Mathf.Min(weights.Length, LivestockNames.ShippedLocales.Length);
		for (int i = 0; i < num2; i++)
		{
			num += weights[i];
		}
		if (num <= 0)
		{
			return Drawable(house);
		}
		int num3 = Random.Range(0, num);
		for (int j = 0; j < num2; j++)
		{
			num3 -= weights[j];
			if (num3 < 0)
			{
				return LivestockNames.ShippedLocales[j];
			}
		}
		return Drawable(house);
	}

	private static string Drawable(string house)
	{
		if (IndexOfLocale(house) < 0)
		{
			return "en";
		}
		return house;
	}

	public static int[] NewTally()
	{
		return new int[LivestockNames.ShippedLocales.Length];
	}

	public static string HouseLocale()
	{
		if (!Livestock.localeNames)
		{
			return "en";
		}
		string namelocale = Livestock.namelocale;
		string text = Translate.ValidateServerLanguage(namelocale);
		if (text != namelocale && warnedLocale != namelocale)
		{
			warnedLocale = namelocale;
			Debug.LogWarning("livestock.namelocale is set to \"" + namelocale + "\", which is not a language this server accepts, so livestock are named in " + text + " instead. The spelling is case sensitive.");
		}
		return text;
	}

	public static int Count(int[] into, string house)
	{
		for (int i = 0; i < into.Length; i++)
		{
			into[i] = 0;
		}
		int num = Add(into, house, Mathf.Max(0, Livestock.baselineNameWeight));
		ListHashSet<BasePlayer> activePlayerList = BasePlayer.activePlayerList;
		for (int j = 0; j < activePlayerList.Count; j++)
		{
			BasePlayer basePlayer = activePlayerList[j];
			if (!(basePlayer == null) && basePlayer.net != null && basePlayer.net.connection != null)
			{
				string locale = Translate.ValidateServerLanguage(basePlayer.net.connection.language);
				if (IndexOfLocale(locale) < 0)
				{
					locale = house;
				}
				num += Add(into, locale, 1);
			}
		}
		return num;
	}

	private static int Add(int[] into, string locale, int weight)
	{
		if (weight <= 0)
		{
			return 0;
		}
		int num = IndexOfLocale(locale);
		if (num < 0)
		{
			num = IndexOfLocale("en");
		}
		if (num < 0)
		{
			return 0;
		}
		into[num] += weight;
		return weight;
	}

	public static int IndexOfLocale(string locale)
	{
		string[] shippedLocales = LivestockNames.ShippedLocales;
		for (int i = 0; i < shippedLocales.Length; i++)
		{
			if (shippedLocales[i] == locale)
			{
				if (!LivestockNames.HasPack(locale))
				{
					return -1;
				}
				return i;
			}
		}
		return -1;
	}
}

