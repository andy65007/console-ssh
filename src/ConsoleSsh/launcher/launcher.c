#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>

/*
 * Portable launcher for machines without a system-wide .NET runtime.
 * It points DOTNET_ROOT at the bundled runtime and forwards the original argv
 * to console-ssh.exe without losing spaces or quotes.
 */
static void append_quoted(wchar_t *command_line, size_t capacity, const wchar_t *argument)
{
    size_t used = wcslen(command_line);
    int needs_quotes = argument[0] == L'\0' || wcspbrk(argument, L" \t\n\v\"") != NULL;
    if (!needs_quotes)
    {
        wcsncat_s(command_line, capacity, argument, _TRUNCATE);
        return;
    }

    if (used + 3 >= capacity)
    {
        return;
    }

    command_line[used++] = L'"';
    command_line[used] = L'\0';
    for (const wchar_t *cursor = argument; ; cursor++)
    {
        size_t backslashes = 0;
        while (*cursor == L'\\')
        {
            cursor++;
            backslashes++;
        }

        if (*cursor == L'\0')
        {
            while (backslashes-- > 0)
            {
                wcsncat_s(command_line, capacity, L"\\\\", _TRUNCATE);
            }
            break;
        }

        if (*cursor == L'"')
        {
            while (backslashes-- > 0)
            {
                wcsncat_s(command_line, capacity, L"\\\\", _TRUNCATE);
            }
            wcsncat_s(command_line, capacity, L"\\\"", _TRUNCATE);
        }
        else
        {
            while (backslashes-- > 0)
            {
                wcsncat_s(command_line, capacity, L"\\", _TRUNCATE);
            }
            wchar_t character[2] = { *cursor, L'\0' };
            wcsncat_s(command_line, capacity, character, _TRUNCATE);
        }
    }

    wcsncat_s(command_line, capacity, L"\"", _TRUNCATE);
}

int wmain(int argc, wchar_t **argv)
{
    wchar_t module_path[32768];
    if (GetModuleFileNameW(NULL, module_path, (DWORD)_countof(module_path)) == 0)
    {
        fwprintf(stderr, L"launcher: GetModuleFileNameW failed (%lu)\n", GetLastError());
        return 1;
    }

    wchar_t *last_separator = wcsrchr(module_path, L'\\');
    if (last_separator == NULL)
    {
        fwprintf(stderr, L"launcher: invalid module path\n");
        return 1;
    }
    *last_separator = L'\0';

    wchar_t runtime_path[32768];
    wchar_t app_host[32768];
    swprintf_s(runtime_path, _countof(runtime_path), L"%ls\\runtime", module_path);
    swprintf_s(app_host, _countof(app_host), L"%ls\\console-ssh.exe", module_path);

    /* The managed apphost probes DOTNET_ROOT and its architecture-specific alias. */
    SetEnvironmentVariableW(L"DOTNET_ROOT", runtime_path);
    SetEnvironmentVariableW(L"DOTNET_ROOT_X64", runtime_path);

    static wchar_t command_line[32768];
    command_line[0] = L'\0';
    append_quoted(command_line, _countof(command_line), app_host);
    for (int index = 1; index < argc; index++)
    {
        wcsncat_s(command_line, _countof(command_line), L" ", _TRUNCATE);
        append_quoted(command_line, _countof(command_line), argv[index]);
    }

    STARTUPINFOW startup_info;
    PROCESS_INFORMATION process_info;
    ZeroMemory(&startup_info, sizeof(startup_info));
    ZeroMemory(&process_info, sizeof(process_info));
    startup_info.cb = sizeof(startup_info);

    if (!CreateProcessW(
            app_host,
            command_line,
            NULL,
            NULL,
            TRUE,
            0,
            NULL,
            module_path,
            &startup_info,
            &process_info))
    {
        fwprintf(stderr, L"launcher: cannot start %ls (%lu)\n", app_host, GetLastError());
        return 1;
    }

    CloseHandle(process_info.hThread);
    WaitForSingleObject(process_info.hProcess, INFINITE);
    DWORD exit_code = 1;
    GetExitCodeProcess(process_info.hProcess, &exit_code);
    CloseHandle(process_info.hProcess);
    return (int)exit_code;
}
