; What folds besides a declaration. Only a construct spanning three lines or more survives, and
; where several start on one line the widest wins. A fold ending on a closing bracket pulls that
; line up behind its chip; one ending on content hides it with the rest.

(compound_statement) @fold
(do_group) @fold
; An if's elif and else fold on their own, so its own fold stops before the first of them.
(if_statement) @fold
(if_statement [(elif_clause) (else_clause)] @stop) @fold
(elif_clause) @fold
(else_clause) @fold
(case_statement) @fold
(case_item) @fold
(subshell) @fold
(array) @fold
(heredoc_body) @fold
