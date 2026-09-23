using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;
using Windows.System;

namespace Win81Layer;

internal static class UserAccount
{
	public static async Task<(ImageSource? Picture, string Name)> LoadAsync()
	{
		string name = Environment.UserName;
		ImageSource pic = null;
		try
		{
			IReadOnlyList<User> users = await User.FindAllAsync(UserType.LocalUser, UserAuthenticationStatus.LocallyAuthenticated);
			User u = null;
			using (IEnumerator<User> enumerator = users.GetEnumerator())
			{
				if (enumerator.MoveNext())
				{
					User candidate = enumerator.Current;
					u = candidate;
				}
			}
			if ((object)u != null)
			{
				name = (await FriendlyName(u)) ?? name;
				pic = await LoadPicture(u);
			}
		}
		catch
		{
		}
		return (Picture: pic, Name: name);
	}

	private static async Task<string?> FriendlyName(User u)
	{
		try
		{
			string display = Prop(await u.GetPropertyAsync(KnownUserProperties.DisplayName));
			if (display != null)
			{
				return display;
			}
			string first = Prop(await u.GetPropertyAsync(KnownUserProperties.FirstName));
			string last = Prop(await u.GetPropertyAsync(KnownUserProperties.LastName));
			if (first != null || last != null)
			{
				return string.Join(' ', new string[2] { first, last }.Where((string s) => s != null));
			}
			string account = Prop(await u.GetPropertyAsync(KnownUserProperties.AccountName));
			if (account != null)
			{
				string result;
				if (!account.Contains('\\'))
				{
					result = account;
				}
				else
				{
					string text = account;
					int num = account.IndexOf('\\') + 1;
					result = text.Substring(num, text.Length - num);
				}
				return result;
			}
		}
		catch
		{
		}
		return null;
		static string? Prop(object? o)
		{
			return (o is string { Length: >0 } s) ? s : null;
		}
	}

	private static async Task<ImageSource?> LoadPicture(User u)
	{
		try
		{
			IRandomAccessStreamReference streamRef = await u.GetPictureAsync(UserPictureSize.Size208x208);
			if (streamRef == null)
			{
				return null;
			}
			using IRandomAccessStreamWithContentType s = await streamRef.OpenReadAsync();
			if (s.Size == 0)
			{
				return null;
			}
			DataReader reader = new DataReader(s);
			await reader.LoadAsync((uint)s.Size);
			byte[] bytes = new byte[s.Size];
			reader.ReadBytes(bytes);
			BitmapImage bmp = new BitmapImage();
			bmp.BeginInit();
			bmp.CacheOption = BitmapCacheOption.OnLoad;
			bmp.StreamSource = new MemoryStream(bytes);
			bmp.EndInit();
			((Freezable)bmp).Freeze();
			return bmp;
		}
		catch
		{
			return null;
		}
	}
}
