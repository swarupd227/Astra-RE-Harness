### Python / FastAPI

Security (the QUALITY gate fails the package on any of these; bandit runs on every package):
- Never put a secret, password, API key or token in code or config. Read it from the environment
  (`os.environ` / pydantic settings) with no default that looks like a real secret.
- SQL is always parameterised (driver placeholders or SQLAlchemy bound parameters); never an f-string, `%` or `+`
  into a query.
- Never call `eval`, `exec`, `pickle.loads`, `yaml.load` without `SafeLoader`, `subprocess` with `shell=True`, or
  `os.system`.
- Outside calls use `https`, keep certificate verification on (never `verify=False`) and set a timeout.
- `hashlib.sha256` or stronger for hashing, `secrets` for tokens; `random` only where security does not matter.
- Never run with `debug=True` or `reload=True` in application code. CORS lists the origins it allows; never `*`
  with credentials.

Coding standards (ruff runs on every package):
- Python 3.12 with type hints on every function. Request and response bodies are pydantic models with field
  constraints (`Field(gt=0)`, `Literal[...]`, `constr`) for the rules the spec states.
- Return errors as `HTTPException` (or a registered exception handler) with the right status code and a message
  that names the problem. Never return a traceback.
- `logging.getLogger(__name__)`, never `print`. Never log secrets or personal data.
- Never a bare `except:` or `except Exception: pass`; catch what you can handle and re-raise with `from`.
- No unused imports or variables, no mutable default arguments, no name shadowing a builtin.
- `Decimal` for money and rates, created from a string, with the rounding the spec states.
- Keep functions short and simple (complexity under 15). Tests are plain pytest functions with clear names.
