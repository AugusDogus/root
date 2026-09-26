"""Locate the active selection in a native per-seat message history."""

def latest_selection(response, initial=None):
    current = initial

    def visit(message):
        nonlocal current
        if isinstance(message, dict):
            name = message.get('name', '')
            if name == 'SelectionFinished':
                current = None
            elif isinstance(name, str) and name.endswith('Required') and 'counter' in message.get('value', {}):
                current = message
            else:
                for value in message.values():
                    visit(value)
        elif isinstance(message, list):
            for value in message:
                visit(value)

    visit(response['messages'])
    return current
