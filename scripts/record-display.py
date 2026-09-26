#!/usr/bin/env python3
"""Record only this lab's Xvfb connection details, then execute its command."""
import json
import os
from pathlib import Path
import sys

output = Path(os.environ['ROOT_LAB_OUTPUT']) / 'display.json'
output.write_text(json.dumps({
    'display': os.environ['DISPLAY'],
    'xauthority': os.environ['XAUTHORITY'],
}) + '\n')
os.execvp(sys.argv[1], sys.argv[1:])
