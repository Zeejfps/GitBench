; What folds besides a declaration. Only a construct spanning three lines or more survives, and
; where several start on one line the widest wins. A fold ending on a closing bracket pulls that
; line up behind its chip; one ending on content hides it with the rest.

; A compound statement's node runs on through its elif, else, except and finally branches, each
; of which folds on its own, so each branch's fold stops at its own block.
(if_statement consequence: (_) @end) @fold
(elif_clause) @fold
(else_clause) @fold
(for_statement body: (_) @end) @fold
(while_statement body: (_) @end) @fold
(try_statement body: (_) @end) @fold
(except_clause) @fold
(finally_clause) @fold
(with_statement) @fold
(match_statement) @fold
(case_clause) @fold

(dictionary) @fold
(list) @fold
(set) @fold
(tuple) @fold
(list_comprehension) @fold
(dictionary_comprehension) @fold
(argument_list) @fold
(parameters) @fold
(parenthesized_expression) @fold

(string) @fold
