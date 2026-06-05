(async () => {
  const text = document.body.innerText || '';
  const requiredText = [
    'Prototype workspace',
    'Open source survey',
    'Goal JSON',
    'Stop dashboard for build/test',
    'Setup Doctor'
  ];

  for (const value of requiredText) {
    if (!text.includes(value)) {
      throw new Error(`Missing dashboard text: ${value}`);
    }
  }

  const sourceSurveyLink = Array.from(document.querySelectorAll('a'))
    .find(anchor => anchor.textContent?.includes('Open source survey'));
  if (!sourceSurveyLink) {
    throw new Error('Source survey link was not rendered.');
  }

  const sourceSurvey = await fetch(sourceSurveyLink.href + '?max=25', { cache: 'no-store' });
  if (!sourceSurvey.ok) {
    throw new Error(`Source survey failed with ${sourceSurvey.status}.`);
  }

  const survey = await sourceSurvey.json();
  const files = survey.Files || [];
  const excluded = files.filter(file =>
    file.includes('/bin/') ||
    file.includes('/obj/') ||
    file.startsWith('.scratch/') ||
    file.startsWith('.orchestrator-prototype/'));
  if (excluded.length > 0) {
    throw new Error(`Source survey included excluded paths: ${excluded.join(', ')}`);
  }

  const goalLink = Array.from(document.querySelectorAll('a'))
    .find(anchor => anchor.textContent?.includes('Goal JSON'));
  if (!goalLink) {
    throw new Error('Goal JSON link was not rendered.');
  }

  const goalDetail = await fetch(goalLink.href, { cache: 'no-store' });
  if (!goalDetail.ok) {
    throw new Error(`Goal detail failed with ${goalDetail.status}.`);
  }

  const detail = await goalDetail.json();
  if (!detail.Goal?.Id || !Array.isArray(detail.Tasks)) {
    throw new Error(`Goal detail shape was incomplete: ${JSON.stringify(detail)}`);
  }

  return {
    goalId: detail.Goal.Id,
    taskCount: detail.Tasks.length,
    sourceSurveyReturnedFiles: survey.ReturnedFiles,
    sourceSurveyTotalMatchedFiles: survey.TotalMatchedFiles
  };
})()
