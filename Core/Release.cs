namespace ArdUi;

static class Release
{
    public static void Verify(string manifest,string signature,string binary)
    {
        using var stream=typeof(Release).Assembly.GetManifestResourceStream("ArdUi.release-public.pem") ?? throw new IOException("缺少发布公钥。");
        using var reader=new StreamReader(stream); using var rsa=RSA.Create(); rsa.ImportFromPem(reader.ReadToEnd());
        var content=File.ReadAllBytes(manifest);
        if(!rsa.VerifyData(content,Convert.FromBase64String(File.ReadAllText(signature).Trim()),HashAlgorithmName.SHA256,RSASignaturePadding.Pss))
            throw new IOException("发布签名无效，拒绝更新。");
        using var document=JsonDocument.Parse(content); var release=document.RootElement;
        using var file=File.OpenRead(binary);
        if(release.GetProperty("size").GetInt64()!=file.Length ||
            !string.Equals(release.GetProperty("sha256").GetString(),Convert.ToHexString(SHA256.HashData(file)),StringComparison.OrdinalIgnoreCase))
            throw new IOException("发布文件校验失败。");
    }
}
