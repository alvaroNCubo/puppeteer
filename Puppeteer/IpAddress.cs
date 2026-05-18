using System;

namespace Puppeteer
{
	public class IpAddress
	{
		private static readonly string DEFAULT_IP_AS_STRING = "0.0.0.0";
		public static readonly IpAddress DEFAULT = new IpAddress(DEFAULT_IP_AS_STRING);
		public const string HEADER_HTTP_INCAP_CLIENT_IP = "HTTP_INCAP_CLIENT_IP";
		private readonly string ip;


		public IpAddress(string ip)
		{
			var standardIp = ValidaYEstandarizaCreacionDeIP(ip);

			this.ip = standardIp;
		}

		public static IpAddress GenerateIpBasedOn(string ipNumbersSerializated)
		{
			if (ipNumbersSerializated.Equals("::1") || ipNumbersSerializated.Equals("localhost") || ipNumbersSerializated.IndexOf(':') != -1)
			{
				return DEFAULT;
			}
			return new IpAddress(ipNumbersSerializated);
		}

		private string ValidaYEstandarizaCreacionDeIP(string ip)
		{
			System.Net.IPAddress ipAddress;
			if (!System.Net.IPAddress.TryParse(ip, out ipAddress)) throw new Exception("The Ip Address is not valid.");
			return ipAddress.ToString();
		}

		public string Ip
		{
			get
			{
				return ip;
			}
		}

		public static string GetIpFromINCAPHedaer(string ip)
		{
			if (string.IsNullOrEmpty(ip)) throw new ArgumentException(nameof(ip));

			return LeftSideoFComma(ip);
		}

		static string LeftSideoFComma(string stringLit)
		{
			int index = stringLit.IndexOf(',');
			if (index == -1) return stringLit;
			index--;
			while (index >= 0 && (stringLit[index] == ' ' || stringLit[index] == '\t' || stringLit[index] == '\r' || stringLit[index] == '\n')) index--;

			if (index == -1) return "";

			return stringLit.Substring(0, index + 1);
		}

		public static string GetIpFromForwarded(string ip)
		{
			if (string.IsNullOrEmpty(ip)) throw new ArgumentException(nameof(ip));

			return LeftSideoFComma(ip);
		}
	}


}
