; What folds besides a declaration. Only a construct spanning three lines or more survives, and
; where several start on one line the widest wins. A fold ending on a closing bracket pulls that
; line up behind its chip; one ending on content hides it with the rest.

(compound_statement) @fold
(field_declaration_list) @fold
(enumerator_list) @fold
(initializer_list) @fold
(declaration_list) @fold

(argument_list) @fold
(parameter_list) @fold

(preproc_if) @fold
(preproc_ifdef) @fold
(comment) @fold
