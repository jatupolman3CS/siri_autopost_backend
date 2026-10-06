import os, re
import psycopg2

ENV = os.environ.get("SIRI_DB_ENV", r"C:\ProjectAutoPost\siri_autopost_backend\.env")


def connect(dbname=None):
    """Connection of ConnectionStrings__Default in the backend .env, with the database name swapped when given."""
    env = open(ENV, encoding="utf-8").read()
    cs = re.search(r"^ConnectionStrings__Default=(.*)$", env, re.M).group(1)
    kv = dict(p.split("=", 1) for p in cs.strip().split(";") if "=" in p)
    return psycopg2.connect(host=kv["Host"], port=kv["Port"], dbname=dbname or kv["Database"],
                            user=kv["Username"], password=kv["Password"], connect_timeout=20)
