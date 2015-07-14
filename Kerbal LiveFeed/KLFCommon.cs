﻿using System;
using System.Linq;
using System.Reflection;
using System.Text;

public class KLFCommon
{
    public const bool COMMON_LOG_ENABLED = true;
    // "BUILD VER"
    public const String PROGRAM_VERSION = "0.9.2";
	public const Int32 FILE_FORMAT_VERSION = 8;
	public const Int32 NET_PROTOCOL_VERSION = 9;
	public const int MSG_HEADER_LENGTH = 8;
	public const int INTEROP_MSG_HEADER_LENGTH = 8;

	public const int SERVER_SETTINGS_LENGTH = 13;

	public const int MAX_CRAFT_FILE_BYTES = 1024 * 1024;

	public const String SHARE_CRAFT_COMMAND = "/sharecraft";
	public const String GET_CRAFT_COMMAND = "!getcraft";

	public const byte CRAFT_TYPE_VAB = 0;
	public const byte CRAFT_TYPE_SPH = 1;

	internal static string filteredFileName(string filename)
	{
		const String illegal = "\\/:*?\"<>|";

 		StringBuilder sb = new StringBuilder();
		foreach (char c in filename)
		{
			//Filter illegal characters out of the player name
			if (!illegal.Contains(c))
				sb.Append(c);
		}

		return sb.ToString();
	}

	public static byte[] intToBytes(Int32 val)
	{
		byte[] bytes = new byte[4];
		bytes[0] = (byte)(val & byte.MaxValue);
		bytes[1] = (byte)((val >> 8) & byte.MaxValue);
		bytes[2] = (byte)((val >> 16) & byte.MaxValue);
		bytes[3] = (byte)((val >> 24) & byte.MaxValue);

		return bytes;
	}

	public static Int32 intFromBytes(byte[] bytes, int offset = 0)
	{
		Int32 val = 0;
		val |= bytes[offset];
		val |= ((Int32)bytes[offset + 1]) << 8;
		val |= ((Int32)bytes[offset + 2]) << 16;
		val |= ((Int32)bytes[offset + 3]) << 24;

		return val;
	}

	public enum ClientMessageID
	{
		HANDSHAKE /*Username Length : Username : Version*/,
		PRIMARY_PLUGIN_UPDATE /*data*/,
		SECONDARY_PLUGIN_UPDATE /*data*/,
		TEXT_MESSAGE /*Message text*/,
		SCREEN_WATCH_PLAYER /*Byte - Send Screenshot : Int32 Index : Int32 Current Index :  Player name*/,
		SCREENSHOT_SHARE /*Screenshot*/,
		KEEPALIVE,
		CONNECTION_END /*Message*/ ,
		UDP_PROBE,
		NULL,
		SHARE_CRAFT_FILE /*Craft Type Byte : Craft name length : Craft Name : File bytes*/,
		ACTIVITY_UPDATE_IN_GAME,
		ACTIVITY_UPDATE_IN_FLIGHT,
		PING
	}

	public enum ServerMessageID
	{
		HANDSHAKE /*Protocol Version : Version String Length : Version String : ClientID*/,
		HANDSHAKE_REFUSAL /*Refusal message*/,
		SERVER_MESSAGE /*Message text*/,
		TEXT_MESSAGE /*Message text*/,
		PLUGIN_UPDATE /*data*/,
		SERVER_SETTINGS /*UpdateInterval (4) : Screenshot Interval (4) : Screenshot Height (4): InactiveShips (1)*/,
		SCREENSHOT_SHARE /*Screenshot*/,
		KEEPALIVE,
		CONNECTION_END /*Message*/,
		UDP_ACKNOWLEDGE,
		NULL,
		CRAFT_FILE /*Craft Type Byte : Craft name length : Craft Name : File bytes*/,
		PING_REPLY
	}

	public enum ClientInteropMessageID
	{
		NULL,
		CLIENT_DATA /*Byte - Inactive Vessels Per Update : Screenshot Height : UpdateInterval : Player Name*/,
		SCREENSHOT_RECEIVE /*Screenshot*/,
		CHAT_RECEIVE /*Message*/,
		PLUGIN_UPDATE /*data*/
	}

	public enum PluginInteropMessageID
	{
		NULL,
		PLUGIN_DATA /*Byte - In-Flight : Int32 - Current Game Title length : Current Game Title*/,
		SCREENSHOT_SHARE /*Screenshot*/,
		CHAT_SEND /*Message*/,
		PRIMARY_PLUGIN_UPDATE /*data*/,
		SECONDARY_PLUGIN_UPDATE /*data*/,
		SCREENSHOT_WATCH_UPDATE /*Int32 index : Int32 current index : Watch Name*/
	}

    private static MethodInfo UnityEngineLogMethodInfo;
    private static bool UseUnityLog = true;

    public static void Log(string message)
    {
        if(UseUnityLog && ((object)UnityEngineLogMethodInfo) == null)
        {
            Type UnityEngineDebugType = Type.GetType(Assembly.CreateQualifiedName("UnityEngine", "UnityEngine.Debug"));
            object UnityEngineDebugTypeCheck = UnityEngineDebugType;

            if(UnityEngineDebugTypeCheck != null)
            {
                UnityEngineLogMethodInfo = UnityEngineDebugType.GetMethod("Log", BindingFlags.Static | BindingFlags.Public, null, new Type[] { typeof(object) }, null);
                object UnityEngineLogMethodInfoCheck = UnityEngineLogMethodInfo;

                if(UnityEngineLogMethodInfoCheck == null)
                {
                    UseUnityLog = false;
                }
            }
            else
            {
                UseUnityLog = false;
            }
        }

        if(COMMON_LOG_ENABLED)
        {
            message = string.Format("[KerbalLiveFeed] {0}", message);

            if(UseUnityLog)
            {
                UnityEngineLogMethodInfo.Invoke(null, new object[] { message });
            }
            else
            {
                Console.WriteLine(message);
            }
        }
    }

    public static void Log(string format, params object[] args)
    {
        KLFCommon.Log(string.Format(format, args));
    }
}

