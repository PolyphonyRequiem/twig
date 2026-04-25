import traceback, sys

import conductor.executor.template as tmpl
TR = tmpl.TemplateRenderer
original_render = TR.render

def debug_render(self, template_str, context, **kwargs):
    try:
        return original_render(self, template_str, context, **kwargs)
    except Exception as e:
        print(f"TEMPLATE FAILED: {repr(template_str)[:200]}", file=sys.stderr)
        if isinstance(context, dict):
            print(f"CONTEXT KEYS: {sorted(context.keys())}", file=sys.stderr)
            if 'workflow' in context:
                w = context['workflow']
                if isinstance(w, dict):
                    print(f"  workflow keys: {sorted(w.keys())}", file=sys.stderr)
                    if 'input' in w:
                        print(f"  workflow.input: {w['input']}", file=sys.stderr)
                else:
                    print(f"  workflow type: {type(w)}", file=sys.stderr)
                    if hasattr(w, 'input'):
                        print(f"  workflow.input: {w.input}", file=sys.stderr)
        traceback.print_exc(file=sys.stderr)
        raise

TR.render = debug_render

from conductor.cli import app
sys.argv = ['conductor', 'run',
    r'C:\Users\dangreen\.conductor\registries\twig\recursive\twig-sdlc-full.yaml',
    '--input', 'work_item_id=1945', '--no-interactive']
try:
    app()
except SystemExit:
    pass
