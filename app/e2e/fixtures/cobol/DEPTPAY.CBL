       IDENTIFICATION DIVISION.
       PROGRAM-ID. DEPTPAY.
      *> Calibrated against openmainframeproject/cobol-programming-course.
      *> Reads a department payroll file, computes per-department averages,
      *> writes one summary record per department.  The AVERAGE-SALARY
      *> paragraph is the 5847.95 trap source: COMPUTE WS-AVG ROUNDED.
       ENVIRONMENT DIVISION.
       INPUT-OUTPUT SECTION.
       FILE-CONTROL.
           SELECT PAYROLL-IN ASSIGN TO PAYROLLF
              FILE STATUS IS WS-STATUS.
           SELECT SUMMARY-OUT ASSIGN TO SUMMARYF
              FILE STATUS IS WS-STATUS.
       DATA DIVISION.
       FILE SECTION.
       FD  PAYROLL-IN.
       01  PAYROLL-REC.
           05  PR-DEPT       PIC X(4).
           05  PR-EMP-ID     PIC 9(6).
           05  PR-SALARY     PIC 9(7)V99 COMP-3.
       FD  SUMMARY-OUT.
       01  SUMMARY-REC.
           05  SUM-DEPT      PIC X(4).
           05  SUM-COUNT     PIC 9(4).
           05  SUM-AVG       PIC 9(7)V99.
       WORKING-STORAGE SECTION.
       01  WS-STATUS         PIC X(2).
       01  WS-TOTAL          PIC 9(11)V99 VALUE ZERO.
       01  WS-COUNT          PIC 9(7)     VALUE ZERO.
       01  WS-AVG            PIC 9(7)V99.
       01  WS-EOF-FLAG       PIC X        VALUE 'N'.
           88 IS-EOF                VALUE 'Y'.
       PROCEDURE DIVISION.
       MAIN-PARA.
           OPEN INPUT  PAYROLL-IN
                OUTPUT SUMMARY-OUT.
           PERFORM UNTIL IS-EOF
              READ PAYROLL-IN
                 AT END SET IS-EOF TO TRUE
                 NOT AT END
                    ADD PR-SALARY TO WS-TOTAL
                    ADD 1         TO WS-COUNT
              END-READ
           END-PERFORM.
           PERFORM AVERAGE-SALARY.
           MOVE WS-AVG    TO SUM-AVG.
           MOVE WS-COUNT  TO SUM-COUNT.
           WRITE SUMMARY-REC.
           CLOSE PAYROLL-IN SUMMARY-OUT.
           STOP RUN.
       AVERAGE-SALARY.
      *> The off-by-one trap: 111111.11 / 19 = 5847.953157...
      *> ROUNDED at scale 2 is 5847.95 -- NOT 5848.06.
           IF WS-COUNT = 0
              MOVE 0 TO WS-AVG
           ELSE
              COMPUTE WS-AVG ROUNDED = WS-TOTAL / WS-COUNT
                 ON SIZE ERROR MOVE 0 TO WS-AVG
              END-COMPUTE
           END-IF.
