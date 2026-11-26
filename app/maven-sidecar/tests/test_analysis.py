"""Parsers and severity mapping of the static-analysis step (no tools needed)."""
from maven_sidecar import analysis

SPOTBUGS_XML = """<?xml version="1.0" encoding="UTF-8"?>
<BugCollection version="4.8.6">
  <BugInstance type="SQL_INJECTION_JDBC" priority="1" rank="3" abbrev="SECSQLIJDBC" category="SECURITY">
    <ShortMessage>Potential JDBC Injection</ShortMessage>
    <LongMessage>This use of java.sql.Statement.executeQuery(String) can be vulnerable to SQL injection</LongMessage>
    <SourceLine classname="com.example.Repo" start="42" end="42" sourcefile="Repo.java" sourcepath="com/example/Repo.java"/>
  </BugInstance>
  <BugInstance type="NP_NULL_ON_SOME_PATH" priority="2" rank="12" category="CORRECTNESS">
    <ShortMessage>Possible null pointer dereference</ShortMessage>
    <SourceLine classname="com.example.Svc" start="7" end="7" sourcepath="com/example/Svc.java"/>
  </BugInstance>
</BugCollection>"""

PMD_JSON = """{"formatVersion":0,"pmdVersion":"7.7.0","files":[
 {"filename":"/var/tmp/maven-runs/analyze-1/src/main/java/com/example/Svc.java","violations":[
  {"beginline":10,"description":"Avoid unused local variables such as 'x'.","rule":"UnusedLocalVariable","ruleset":"Best Practices","priority":3},
  {"beginline":3,"description":"Possible God Class","rule":"GodClass","ruleset":"Design","priority":3},
  {"beginline":5,"description":"Hard coded crypto key","rule":"HardCodedCryptoKey","ruleset":"Security","priority":3}
 ]}]}"""

CHECKSTYLE_XML = """<?xml version="1.0" encoding="UTF-8"?>
<checkstyle version="10.20.1">
<file name="/var/tmp/maven-runs/analyze-1/src/main/java/com/example/Svc.java">
<error line="4" column="1" severity="warning" message="Using the '.*' form of import should be avoided - java.util.*." source="com.puppycrawl.tools.checkstyle.checks.imports.AvoidStarImportCheck"/>
<error line="20" severity="warning" message="switch without &quot;default&quot; clause." source="com.puppycrawl.tools.checkstyle.checks.coding.MissingSwitchDefaultCheck"/>
</file>
</checkstyle>"""

ROOT = "/var/tmp/maven-runs/analyze-1"


def test_spotbugs_security_findings_are_critical_and_paths_are_project_relative():
    f = analysis.parse_spotbugs(SPOTBUGS_XML)
    assert f[0]["rule"] == "SPOTBUGS-SQL_INJECTION_JDBC"
    assert f[0]["category"] == "security" and f[0]["severity"] == "critical"
    assert f[0]["file"] == "src/main/java/com/example/Repo.java" and f[0]["line"] == 42
    assert "SQL injection" in f[0]["message"]
    assert f[1]["category"] == "standards" and f[1]["severity"] == "minor"


def test_a_high_confidence_security_finding_is_critical_even_with_a_low_rank():
    # FindSecBugs reports SQL_INJECTION_JDBC at rank 15 with priority 1 (high confidence)
    assert analysis.spotbugs_severity("SECURITY", 15, priority=1) == "critical"
    assert analysis.spotbugs_severity("SECURITY", 15, priority=2) == "major"
    assert analysis.spotbugs_severity("CORRECTNESS", 15, priority=1) == "info"


def test_pmd_rulesets_map_to_categories_and_severities():
    f = {x["rule"]: x for x in analysis.parse_pmd(PMD_JSON, ROOT)}
    assert f["PMD-UnusedLocalVariable"]["file"] == "src/main/java/com/example/Svc.java"
    assert f["PMD-UnusedLocalVariable"]["category"] == "standards" and f["PMD-UnusedLocalVariable"]["severity"] == "minor"
    assert f["PMD-GodClass"]["category"] == "maintainability"
    assert f["PMD-HardCodedCryptoKey"]["category"] == "security" and f["PMD-HardCodedCryptoKey"]["severity"] == "major"


def test_checkstyle_errors_become_minor_standards_findings():
    f = analysis.parse_checkstyle(CHECKSTYLE_XML, ROOT)
    assert [x["rule"] for x in f] == ["CHECKSTYLE-AvoidStarImport", "CHECKSTYLE-MissingSwitchDefault"]
    assert all(x["severity"] == "minor" and x["category"] == "standards" for x in f)
    assert f[1]["line"] == 20 and f[0]["file"] == "src/main/java/com/example/Svc.java"


def test_empty_reports_give_no_findings():
    assert analysis.parse_spotbugs("") == []
    assert analysis.parse_pmd("", ROOT) == []
    assert analysis.parse_checkstyle("", ROOT) == []


def test_jacoco_csv_gives_line_coverage():
    csv = ("GROUP,PACKAGE,CLASS,INSTRUCTION_MISSED,INSTRUCTION_COVERED,BRANCH_MISSED,BRANCH_COVERED,LINE_MISSED,LINE_COVERED\n"
           "app,com.example,Svc,10,90,1,3,4,36\n"
           "app,com.example,Repo,0,20,0,0,0,10\n")
    assert analysis.coverage_from_csv(csv) == {"lines": 50, "covered": 46, "percent": 92.0}
    assert analysis.coverage_from_csv("") is None
