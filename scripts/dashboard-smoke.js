(async () => {
  // Verify nav bar is present on the current page
  const nav = document.querySelector('.dashboard-nav');
  if (!nav) {
    throw new Error('Nav bar (.dashboard-nav) was not rendered.');
  }

  const navLinks = Array.from(nav.querySelectorAll('a'));
  const navHrefs = navLinks.map(anchor => anchor.getAttribute('href'));
  for (const required of ['/', '/config', '/system']) {
    if (!navHrefs.includes(required)) {
      throw new Error(`Nav bar missing link to ${required}.`);
    }
  }

  // Ops view (current page) should have goal summary
  const opsText = document.body.innerText || '';
  const opsRequired = ['Goal JSON'];
  for (const value of opsRequired) {
    if (!opsText.includes(value)) {
      throw new Error(`Missing ops view text: ${value}`);
    }
  }

  // Fetch config view and verify Setup Doctor
  const configResponse = await fetch('/config', { cache: 'no-store' });
  if (!configResponse.ok) {
    throw new Error(`Config view failed with ${configResponse.status}.`);
  }
  const configHtml = await configResponse.text();
  if (!configHtml.includes('Setup Doctor')) {
    throw new Error('Config view missing Setup Doctor.');
  }

  // Fetch system view and verify workspace/diagnostics
  const systemResponse = await fetch('/system', { cache: 'no-store' });
  if (!systemResponse.ok) {
    throw new Error(`System view failed with ${systemResponse.status}.`);
  }
  const systemHtml = await systemResponse.text();
  const systemRequired = [
    'Prototype workspace',
    'Open bounded source survey',
    'Stop dashboard for build/test'
  ];
  for (const value of systemRequired) {
    if (!systemHtml.includes(value)) {
      throw new Error(`System view missing text: ${value}`);
    }
  }

  // Source survey link check via API
  const sourceSurvey = await fetch('/api/source-survey?max=8', { cache: 'no-store' });
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

  // Goal JSON link check
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
