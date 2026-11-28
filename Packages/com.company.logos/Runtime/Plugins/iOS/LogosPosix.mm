#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

// IL2CPP 为 __Internal 生成与托管签名对应的声明，独立名称避免与系统头的 POSIX 原型冲突。
extern "C" intptr_t logos_ios_realpath(char *path, intptr_t destination)
{
    return reinterpret_cast<intptr_t>(realpath(path, reinterpret_cast<char *>(destination)));
}

extern "C" int32_t logos_ios_renameat(int32_t fromDirectory, char *from, int32_t toDirectory, char *to)
{
    return renameat(fromDirectory, from, toDirectory, to);
}
