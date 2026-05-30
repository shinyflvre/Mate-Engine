using UnityEngine;
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Security.Cryptography;

public static class SteamDRM
{
    static bool initialized;
    static bool entitled;
    static int currentAppId;
    static long expUtcTicks;
    static HashSet<int> dlc = new HashSet<int>();

    [Serializable]
    class TokenData
    {
        public string steamId;
        public int appId;
        public long exp;
        public List<int> dlc;
    }

    static string TokenPath => MateEnginePaths.DataFilePath("SteamDRM.token");

    public static bool Initialized => initialized;
    public static bool IsEntitled => entitled;

    public static void Initialize(int appId, int ttlDays = 14)
    {
        if (initialized && currentAppId == appId) return;
        currentAppId = appId;
        initialized = true;
        if (TryInitLive(appId, ttlDays)) return;
        LoadToken();
        entitled = ValidateToken(appId);
    }

    public static bool TryInitLive(int appId, int ttlDays = 14)
    {
        try
        {
            if (!Steamworks.SteamAPI.Init()) return false;
            if (!Steamworks.SteamUser.BLoggedOn()) return false;
            var owned = Steamworks.SteamApps.BIsSubscribedApp(new Steamworks.AppId_t((uint)appId));
            if (!owned) return false;

            dlc.Clear();
            int c = Steamworks.SteamApps.GetDLCCount();
            for (int i = 0; i < c; i++)
            {
                Steamworks.AppId_t id;
                bool available;
                string name;
                if (Steamworks.SteamApps.BGetDLCDataByIndex(i, out id, out available, out name, 256))
                {
                    if (Steamworks.SteamApps.BIsDlcInstalled(id)) dlc.Add((int)id.m_AppId);
                }
            }

            expUtcTicks = DateTime.UtcNow.AddDays(ttlDays).Ticks;
            var td = new TokenData
            {
                steamId = Steamworks.SteamUser.GetSteamID().ToString(),
                appId = appId,
                exp = expUtcTicks,
                dlc = new List<int>(dlc)
            };
            SaveToken(td);
            entitled = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool HasDLC(int dlcId)
    {
        try
        {
            if (entitled) return Steamworks.SteamApps.BIsDlcInstalled(new Steamworks.AppId_t((uint)dlcId)) || dlc.Contains(dlcId);
        }
        catch { }
        return dlc.Contains(dlcId);
    }

    public static void Invalidate()
    {
        entitled = false;
        expUtcTicks = 0;
        dlc.Clear();
    }

    static bool ValidateToken(int appId)
    {
        if (expUtcTicks <= 0) return false;
        if (DateTime.UtcNow.Ticks >= expUtcTicks) return false;
        if (appId != 0 && currentAppId != 0 && appId != currentAppId) return false;
        return true;
    }

    static void SaveToken(TokenData td)
    {
        var json = JsonUtility.ToJson(td);
        var plain = Encoding.UTF8.GetBytes(json);
        var key = DeriveKey(currentAppId);
        var iv = DeriveIV(currentAppId);
        var cipher = Encrypt(plain, key, iv);
        try { File.WriteAllBytes(TokenPath, cipher); } catch { }
    }

    static void LoadToken()
    {
        expUtcTicks = 0;
        dlc.Clear();
        try
        {
            if (!File.Exists(TokenPath)) { entitled = false; return; }
            var cipher = File.ReadAllBytes(TokenPath);
            var key = DeriveKey(currentAppId);
            var iv = DeriveIV(currentAppId);
            var plain = Decrypt(cipher, key, iv);
            var json = Encoding.UTF8.GetString(plain);
            var td = JsonUtility.FromJson<TokenData>(json);
            if (td == null) { entitled = false; return; }
            expUtcTicks = td.exp;
            dlc = td.dlc != null ? new HashSet<int>(td.dlc) : new HashSet<int>();
            entitled = ValidateToken(td.appId);
        }
        catch
        {
            expUtcTicks = 0;
            dlc.Clear();
            entitled = false;
        }
    }

    static byte[] DeriveKey(int appId)
    {
        var seed = Application.companyName + "|" + Application.productName + "|" + SystemInfo.deviceUniqueIdentifier + "|" + Environment.UserName + "|" + appId.ToString();
        using (var sha = SHA256.Create()) return sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
    }

    static byte[] DeriveIV(int appId)
    {
        var seed = SystemInfo.operatingSystem + "|" + Environment.MachineName + "|" + appId.ToString();
        using (var md5 = MD5.Create()) return md5.ComputeHash(Encoding.UTF8.GetBytes(seed));
    }

    static byte[] Encrypt(byte[] data, byte[] key, byte[] iv)
    {
        using (var aes = Aes.Create())
        {
            aes.Key = key;
            aes.IV = iv.AsSpan(0, 16).ToArray();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using (var enc = aes.CreateEncryptor())
                return enc.TransformFinalBlock(data, 0, data.Length);
        }
    }

    static byte[] Decrypt(byte[] data, byte[] key, byte[] iv)
    {
        using (var aes = Aes.Create())
        {
            aes.Key = key;
            aes.IV = iv.AsSpan(0, 16).ToArray();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using (var dec = aes.CreateDecryptor())
                return dec.TransformFinalBlock(data, 0, data.Length);
        }
    }
}
