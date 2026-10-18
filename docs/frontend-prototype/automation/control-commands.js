'use strict';
function buildControlDecision(i) {
 // Normalize a copy of Core's PascalCase context; preserve opaque evidence and its token.
 const camel=v=>Array.isArray(v)?v.map(camel):v&&typeof v==='object'?Object.fromEntries(Object.entries(v).map(([k,x])=>[k[0].toLowerCase()+k.slice(1),camel(x)])):v;
 const c=camel(JSON.parse(i.contextJson));
 switch(i.kind) {
  case 'repair': {
   if(!c.taskId||!c.part?.runId||!c.part?.headSha||!c.part?.reviewId)throw Error('缺少原返工交付和审核身份');
   return {taskId:c.taskId,partId:i.objectId,expectedPartRevision:i.version,expectedRunId:c.part.runId,expectedHeadSha:c.part.headSha,
    expectedEvidenceToken:i.evidenceToken,expectedDecisionId:c.part.reviewId,reason:''};
  }
  case 'reconsider': {
   if(!c.taskId||!c.part?.runId||!c.part?.headSha||!c.part?.reviewId)throw Error('缺少原交付或原决定身份');
   return {taskId:c.taskId,partId:i.objectId,expectedPartRevision:i.version,expectedRunId:c.part.runId,expectedHeadSha:c.part.headSha,
    expectedEvidenceToken:i.evidenceToken,expectedDecisionId:c.part.reviewId,reason:''};
  }
  case 'review': {
   if(!c.taskId||!c.part?.runId||!c.part?.headSha)throw Error('缺少已停止交付的任务、运行或提交身份');
   return {taskId:c.taskId,partId:i.objectId,expectedPartRevision:i.version,expectedRunId:c.part.runId,expectedHeadSha:c.part.headSha,
    expectedEvidenceToken:i.evidenceToken,decision:'Defer',findings:'',evidenceJson:''};
  }
  case 'plan': return {action:'ApprovePackage',packageId:i.objectId,expectedPackageVersion:i.version};
  case 'integration': return {action:'Authorize',packageId:i.objectId,expectedPackageVersion:i.version,expectedEvidenceToken:i.evidenceToken};
  case 'authorization': return {action:'Revoke',packageId:c.packageId,expectedPackageVersion:c.packageVersion,expectedEvidenceToken:i.evidenceToken,authorizationId:c.id,reason:''};
  case 'knowledge': return {action:i.options.includes('publish')?'Publish':'RejectProposal',proposalId:i.objectId,expectedKnowledgeVersion:i.version,reason:''};
  case 'question': return {action:'Answer',questionId:i.objectId,text:'',expectedFromVersion:c.fromVersion,expectedToVersion:c.toVersion};
  case 'restructure': return {action:i.options.includes('approve')?'Approve':'Reject',proposalId:i.objectId,reason:''};
  case 'choice': {
   const candidate=c.members.find(m=>m.approved&&m.accepted&&m.taskId);
   if(!candidate)throw Error('没有已接受的候选成果');
   return {action:'SelectWinner',groupId:i.objectId,winner:candidate.packageId,expectedEvidenceToken:candidate.evidenceToken};
  }
  case 'adoption': return {action:'Adopt',packageId:i.objectId,expectedPackageVersion:i.version,key:c.adoption.key,expectedKnowledgeVersion:c.adoption.knowledgeVersion};
  default: throw Error('此决定仍由受信审核入口处理');
 }
}
if(typeof module!=='undefined')module.exports={buildControlDecision};
