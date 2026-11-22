using System;

namespace Logos.Storage
{
    // 在调用方给定的字符区间内覆盖匹配的凭据，对匹配次数也设置上限，使单条输入的脱敏工作有界。
    /// <summary>在给定有界存储中掩蔽常见凭据形式。无法识别任意个人信息。</summary>
    public sealed class CredentialTextSanitizer : ILogTextSanitizer
    {
        private static readonly string[] Keys = { "authorization", "password", "token" };

        public bool TrySanitize(string input, char[] output, int capacity, out int written)
        {
            written = 0;
            if (
                input == null
                || output == null
                || capacity < 0
                || capacity > output.Length
                || input.Length > capacity
            )
                return false;
            input.CopyTo(0, output, 0, input.Length);
            int matches = 0;
            for (int i = 0; i < input.Length; i++)
            {
                if (i > 0 && (char.IsLetterOrDigit(input[i - 1]) || input[i - 1] == '_'))
                    continue;
                foreach (string key in Keys)
                {
                    if (
                        i + key.Length > input.Length
                        || string.Compare(
                            input,
                            i,
                            key,
                            0,
                            key.Length,
                            StringComparison.OrdinalIgnoreCase
                        ) != 0
                    )
                        continue;
                    int value = i + key.Length;
                    while (value < input.Length && (input[value] == ' ' || input[value] == '\t'))
                        value++;
                    if (value == input.Length || (input[value] != '=' && input[value] != ':'))
                        continue;
                    value++;
                    while (value < input.Length && (input[value] == ' ' || input[value] == '\t'))
                        value++;
                    int end = value;
                    while (
                        end < input.Length
                        && input[end] != '\r'
                        && input[end] != '\n'
                        && input[end] != ','
                        && input[end] != ';'
                        && (key == "authorization" || !char.IsWhiteSpace(input[end]))
                    )
                        end++;
                    if (++matches > 128)
                    {
                        Array.Clear(output, 0, input.Length);
                        return false;
                    }
                    for (int j = value; j < end; j++)
                        output[j] = '*';
                    i = Math.Max(i, end - 1);
                    break;
                }
            }
            written = input.Length;
            return true;
        }
    }
}
