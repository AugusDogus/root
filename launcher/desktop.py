"""Desktop entry point. The launcher opens Root, with errors shown by the OS."""
import ctypes
import sys
import shutil
import subprocess
import traceback

from main import data_home, main


def run():
    try:
        data = data_home()
        data.mkdir(parents=True, exist_ok=True)
        # A windowed executable has no console. Keep diagnostics in the user's
        # own data folder and show a plain error if startup cannot open Root.
        with (data / 'launcher.log').open('a', encoding='utf-8') as log:
            sys.stdout = log
            sys.stderr = log
            try:
                main()
            except Exception:
                traceback.print_exc()
                raise
    except Exception as error:
        message = f'Root Six Player could not open.\n\n{error}\n\nYour game and saved matches have not been removed.'
        if sys.platform == 'win32':
            ctypes.windll.user32.MessageBoxW(None, message, 'Root Six Player', 0x10)
        elif shutil.which('zenity'):
            subprocess.run(['zenity', '--error', '--title=Root Six Player', '--text=' + message, '--no-markup'])
        else:
            print(message, file=sys.__stderr__)
        return 1
    finally:
        sys.stdout = sys.__stdout__
        sys.stderr = sys.__stderr__
    return 0


if __name__ == '__main__':
    raise SystemExit(run())
