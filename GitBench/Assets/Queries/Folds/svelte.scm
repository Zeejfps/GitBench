; What folds besides a declaration. Only a construct spanning three lines or more survives, and
; where several start on one line the widest wins. A fold ending on a closing bracket pulls that
; line up behind its chip; one ending on content hides it with the rest.

(element) @fold
(script_element) @fold
(style_element) @fold
(if_statement) @fold
(each_statement) @fold
(await_statement) @fold
(key_statement) @fold
(snippet_statement) @fold
(comment) @fold
