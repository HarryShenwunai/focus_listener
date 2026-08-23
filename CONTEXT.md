# Focus Listener

Focus Listener is a personal classroom companion that helps a learner return attention to the lesson through brief, content-grounded interactions.

## Language

**Learner（学习者）**:
The person attending the lesson who runs Focus Listener and answers its prompts. The learner is the primary user.
_Avoid_: Teacher, presenter, classroom operator

**Attention Reset（注意力复位）**:
A brief intervention that redirects the learner's attention to the lesson content currently being taught. It is the product's sole primary outcome.
_Avoid_: Assessment, examination, engagement score

**Reset Question（复位题）**:
A short, lesson-grounded multiple-choice interaction used to produce an Attention Reset. Checking comprehension is its mechanism, not its primary purpose.
_Avoid_: Test question, exam question, teacher-published question

**Knowledge Unit（知识单元）**:
A semantically complete relationship stated in the current lesson: a definition, cause, rule or condition, process or sequence, comparison or distinction, or classification or example. It is subject-agnostic and must be grounded in a continuous transcript excerpt.
_Avoid_: Topic keyword, transcript fragment, externally corrected fact

**Question Candidate（题目候选）**:
A Knowledge Unit that passed all hard question and evidence rules and can wait in the bounded candidate pool. A candidate is not yet a displayed Reset Question.
_Avoid_: Pending answer, generated popup, raw transcript

**Candidate Ready（题目已准备）**:
The learner-visible state that at least one Question Candidate can be shown immediately without waiting for a new transcript turn.
_Avoid_: Question displayed, answer pending

**Lesson Session（课堂会话）**:
The period during which the Learner intentionally keeps Focus Listener listening to one lesson. It continues until the Learner ends it; a reminder never ends it automatically.
_Avoid_: Recording, fixed-duration test, background monitoring

**Session Reminder（课堂提醒）**:
An optional one-time notice that a Lesson Session has reached the learner-selected elapsed time. It is not a timeout and does not stop listening.
_Avoid_: Session limit, automatic stop

**Lesson Evidence（课堂证据）**:
A short continuous excerpt from the current lesson that directly supports a Reset Question and its correct choice.
_Avoid_: Full transcript, model explanation, external citation

**Authorized Lesson Content（获授权课堂内容）**:
Lesson audio the Learner has the right and permission to capture and submit to the configured model provider.
_Avoid_: Publicly accessible content, assumed consent
