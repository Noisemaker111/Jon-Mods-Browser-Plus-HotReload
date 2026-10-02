"""Bounded evaluator for XUi arithmetic/binding expressions. No eval or code execution."""
import ast
import math
import operator
import re


def _rewrite(expression):
    # Translate the game's ternary syntax, preserving nesting and quoted strings.
    parts, start, depth, quote, escaped = [], 0, 0, None, False
    for i, char in enumerate(expression):
        if quote:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == quote:
                quote = None
        elif char in "\"'":
            quote = char
        elif char == "(":
            if depth == 0:
                parts.append(expression[start:i + 1])
                start = i + 1
            depth += 1
        elif char == ")":
            depth -= 1
            if depth == 0:
                parts.append(_rewrite(expression[start:i]) + ")")
                start = i + 1
    if depth != 0 or quote:
        raise ValueError("Unbalanced expression")
    parts.append(expression[start:])
    expression = "".join(parts)
    depth, quote, escaped, question, nested = 0, None, False, None, 0
    for i, char in enumerate(expression):
        if quote:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == quote:
                quote = None
        elif char in "\"'":
            quote = char
        elif char == "(":
            depth += 1
        elif char == ")":
            depth -= 1
        elif depth == 0 and char == "?":
            if question is None:
                question = i
            else:
                nested += 1
        elif depth == 0 and char == ":" and question is not None:
            if nested:
                nested -= 1
            else:
                condition = _rewrite(expression[:question])
                yes, no = _rewrite(expression[question + 1:i]), _rewrite(expression[i + 1:])
                return f"({yes} if {condition} else {no})"
    # Operator replacement outside string literals only.
    tokens = re.split(r"('(?:\\.|[^'\\])*'|\"(?:\\.|[^\"\\])*\")", expression)
    for i in range(0, len(tokens), 2):
        tokens[i] = re.sub(r"!(?!=)", " not ", tokens[i]).replace("&&", " and ").replace("||", " or ")
        tokens[i] = re.sub(r"(?<![=!<>])=(?!=)", "==", tokens[i])
    return "".join(tokens).strip()


def evaluate(expression, variables, localization=None):
    if len(expression) > 4000:
        raise ValueError("Expression too long")
    def value(item):
        if not isinstance(item, str):
            return item
        if item.lower() in ("true", "false"):
            return item.lower() == "true"
        try:
            return float(item) if "." in item else int(item)
        except ValueError:
            return item
    variables = {key: value(v) for key, v in variables.items()}
    variables.update(true=True, false=False, null=None)
    ops = {ast.Add: operator.add, ast.Sub: operator.sub, ast.Mult: operator.mul, ast.Div: operator.truediv,
           ast.Mod: operator.mod, ast.USub: operator.neg, ast.UAdd: operator.pos, ast.Not: operator.not_,
           ast.Eq: operator.eq, ast.NotEq: operator.ne, ast.Lt: operator.lt, ast.LtE: operator.le,
           ast.Gt: operator.gt, ast.GtE: operator.ge}
    calls = {"Round": round, "Floor": math.floor, "Ceil": math.ceil, "Min": min, "Max": max,
             "Abs": abs, "int": lambda v: int(float(v)), "float": float, "str": str, "length": len,
             "defined": lambda key: key in variables, "color": lambda *args: ",".join(map(str, args)),
             "localization": lambda key: (localization or {}).get(key, key)}
    steps = 0
    def visit(node):
        nonlocal steps
        steps += 1
        if steps > 300:
            raise ValueError("Expression too complex")
        if isinstance(node, ast.Constant):
            return node.value
        if isinstance(node, ast.Name):
            if node.id not in variables:
                raise ValueError("Missing binding: " + node.id)
            return variables[node.id]
        if isinstance(node, ast.BinOp) and type(node.op) in ops:
            left, right = visit(node.left), visit(node.right)
            if isinstance(node.op, ast.Mult) and (isinstance(left, str) or isinstance(right, str)):
                raise ValueError("String multiplication not supported")
            return ops[type(node.op)](left, right)
        if isinstance(node, ast.UnaryOp) and type(node.op) in ops:
            return ops[type(node.op)](visit(node.operand))
        if isinstance(node, ast.IfExp):
            return visit(node.body if visit(node.test) else node.orelse)
        if isinstance(node, ast.BoolOp):
            for child in node.values:
                result = visit(child)
                if isinstance(node.op, ast.And) and not result or isinstance(node.op, ast.Or) and result:
                    return result
            return result
        if isinstance(node, ast.Compare):
            left = visit(node.left)
            for op, right in zip(node.ops, node.comparators):
                right = visit(right)
                if type(op) not in ops or not ops[type(op)](left, right):
                    return False
                left = right
            return True
        if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id in calls and not node.keywords and len(node.args) < 20:
            return calls[node.func.id](*(visit(arg) for arg in node.args))
        raise ValueError("Unsupported expression")
    return visit(ast.parse(_rewrite(expression), mode="eval").body)
