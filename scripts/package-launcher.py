#!/usr/bin/env python3
"""Build the native friend launchers under the development resource budget."""
from pathlib import Path
import runpy

if __name__ == '__main__':
    runpy.run_path(str(Path(__file__).with_name('package-native-launcher.py')), run_name='__main__')
